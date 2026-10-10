using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingWebhookBrokerTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>
{
    [PricingWebhookBrokerFact]
    public async Task Outbox_Rabbit_consumer_and_real_http_receiver_support_failure_and_manual_retry()
    {
        await database.ResetAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-pricing-webhook", ClientName = prefix };
        var webhook = new EventSubscription { EventName = PricingRecalculationCompletedV1.Name, ConsumerName = prefix + "-webhook" };
        var committed = new EventSubscription { EventName = PriceQuoteCommittedV1.Name, ConsumerName = prefix + "-facts" };
        var topology = EventTopology.Create(broker.ExchangeName, [webhook, committed]);
        var statePath = Path.Combine(Path.GetTempPath(), $"nsn-pricing-webhook-{Guid.NewGuid():N}.json");
        var receiver = await WebhookReceiver.StartAsync(statePath, failFirst: true);
        var settingsEndpoint = new Uri("https://receiver.test/webhooks/pricing");
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNexusStackApplication();
            services.AddPricingPostgres(database.ConnectionString,
                webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = settingsEndpoint.AbsoluteUri, MaxAttempts = 1 });
            services.AddSingleton<HttpClient>(new HttpClient(new RewriteHandler(() => receiver.BaseAddress)));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var scope = provider.CreateAsyncScope();
            await using var consumerScope = provider.CreateAsyncScope();
            var processor = consumerScope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);
            await using var consumer = new RabbitMqConsumer(broker, topology, webhook, processor);
            using var stopped = new CancellationTokenSource();
            var consuming = consumer.RunAsync(stopped.Token);

            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);

            await using var bus = new RabbitMqEventBus(broker);
            var publisher = new OutboxPublisher(scope.ServiceProvider.GetRequiredService<IOutboxStore>(), bus,
                scope.ServiceProvider.GetRequiredService<NexusStackNext.BuildingBlocks.Application.Time.IClock>(),
                new OutboxDeliveryOptions { MaxAttempts = 1 });
            Assert.Equal(3, (await publisher.PublishPendingAsync()).Delivered);
            var runner = scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>();
            await WaitForWebhookAsync(sender, runner, receiver);
            var eventId = receiver.EventId;
            stopped.Cancel();
            await consuming;
            await receiver.DisposeAsync();
            receiver = await WebhookReceiver.StartAsync(statePath, failFirst: false);

            var pending = await sender.QueryAsync(new GetPricingWebhook(eventId));
            Assert.True(pending.IsSuccess);
            Assert.Equal("DeadLettered", pending.Value.State);
            var retry = await sender.SendAsync(new RetryPricingWebhook(eventId, 1));
            Assert.True(retry.IsSuccess);
            Assert.Equal("Pending", retry.Value.State);
            Assert.True(await WaitForSuccessfulDeliveryAsync(runner));
            var succeeded = await sender.QueryAsync(new GetPricingWebhook(eventId));
            Assert.Equal("Succeeded", succeeded.Value.State);
            Assert.Equal(2, receiver.RequestCount(eventId));
            Assert.Equal(1, receiver.EffectCount(eventId));
        }
        finally
        {
            await receiver.DisposeAsync();
            try { File.Delete(statePath); } catch (IOException) { }
            await DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task WaitForWebhookAsync(ISender sender, IPricingWebhookDeliveryRunner runner, WebhookReceiver receiver)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await runner.DeliverPendingAsync(timeout.Token);
            if (receiver.FirstEventId is { } eventId)
            {
                var status = await sender.QueryAsync(new GetPricingWebhook(eventId), timeout.Token);
                if (status.IsSuccess && status.Value.State == "DeadLettered") { return; }
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<bool> WaitForSuccessfulDeliveryAsync(IPricingWebhookDeliveryRunner runner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (await runner.DeliverPendingAsync(timeout.Token) == 1) { return true; }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task DeleteTopologyAsync(RabbitMqOptions broker, EventTopology topology)
    {
        try
        {
            await using var connection = await new RabbitMQ.Client.ConnectionFactory
            {
                HostName = broker.HostName,
                Port = broker.Port,
                UserName = broker.UserName,
                Password = broker.Password,
                VirtualHost = broker.VirtualHost,
            }.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in topology.AllQueueNames) { await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); }
            await channel.ExchangeDeleteAsync(topology.ExchangeName);
        }
        catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException) { }
    }

    private sealed class RewriteHandler(Func<Uri> receiver) : HttpMessageHandler
    {
        private readonly HttpClient _client = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = new HttpRequestMessage(request.Method, new Uri(receiver(), request.RequestUri!.PathAndQuery)) { Content = request.Content };
            foreach (var header in request.Headers) { target.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return _client.SendAsync(target, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _client.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class WebhookReceiver(WebApplication app, PersistentWebhookStore store, bool failFirst) : IAsyncDisposable
    {
        private readonly TaskCompletionSource<Guid> _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failures = failFirst ? 1 : 0;
        public Uri BaseAddress { get; private set; } = null!;
        public Guid? FirstEventId { get; private set; }
        public Guid EventId => _firstFailure.Task.GetAwaiter().GetResult();
        public static async Task<WebhookReceiver> StartAsync(string statePath, bool failFirst)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var receiver = new WebhookReceiver(app, await PersistentWebhookStore.LoadAsync(statePath), failFirst);
            app.MapPost("/webhooks/pricing", receiver.AcceptAsync);
            await app.StartAsync();
            receiver.BaseAddress = new Uri(app.Urls.Single());
            return receiver;
        }
        private async Task AcceptAsync(HttpRequest request, HttpResponse response)
        {
            var message = await request.ReadFromJsonAsync<PricingRecalculationCompletedV1>();
            Assert.NotNull(message);
            var id = message!.EventId;
            FirstEventId = id;
            await store.RecordAsync(id);
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                _firstFailure.TrySetResult(id);
                response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            await response.WriteAsJsonAsync(new PricingWebhookAcceptedV1 { EventId = id, IdempotencyKey = message.IdempotencyKey, Accepted = true });
        }
        public Task<Guid> WaitForFirstFailureAsync() => _firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(20));
        public int RequestCount(Guid eventId) => store.RequestCount(eventId);
        public int EffectCount(Guid eventId) => store.EffectCount(eventId);
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    private sealed class PersistentWebhookStore : IAsyncDisposable
    {
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly State _state;

        private PersistentWebhookStore(string path, State state) { _path = path; _state = state; }

        public static async Task<PersistentWebhookStore> LoadAsync(string path)
        {
            State state;
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                state = await JsonSerializer.DeserializeAsync<State>(stream) ?? new State();
            }
            else { state = new State(); }
            return new PersistentWebhookStore(path, state);
        }

        public async Task RecordAsync(Guid eventId)
        {
            await _gate.WaitAsync();
            try
            {
                _state.Requests[eventId] = _state.Requests.GetValueOrDefault(eventId) + 1;
                _state.Effects.Add(eventId);
                await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(_state));
            }
            finally { _gate.Release(); }
        }

        public int RequestCount(Guid eventId) => _state.Requests.GetValueOrDefault(eventId);
        public int EffectCount(Guid eventId) => _state.Effects.Contains(eventId) ? 1 : 0;
        public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }

        private sealed class State
        {
            public Dictionary<Guid, int> Requests { get; init; } = [];
            public HashSet<Guid> Effects { get; init; } = [];
        }
    }

    private sealed class PricingWebhookBrokerFactAttribute : Xunit.FactAttribute
    {
        public PricingWebhookBrokerFactAttribute()
        {
            if (RabbitMqTestBroker.TryGetOptions() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable)))
            { Skip = "需要真实 PostgreSQL 与 RabbitMQ。"; }
        }
    }
}
