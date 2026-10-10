using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingWebhookOutboxTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Completed_recalculation_adds_one_minimal_webhook_event_to_the_same_outbox()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);

        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);

        var entries = await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var webhook = Assert.Single(entries, entry => entry.EventName == PricingRecalculationCompletedV1.Name);
        var message = serializer.Deserialize<PricingRecalculationCompletedV1>(webhook.Payload);

        Assert.Equal(webhook.Id, message.EventId);
        Assert.Equal(message.EventId, message.IdempotencyKey);
        Assert.Equal(request.ItemId, message.ItemId);
        Assert.Equal(1, message.InputRevision);
        Assert.Equal(1, message.CalculatedRevision);
        Assert.DoesNotContain("80", webhook.Payload);
        Assert.DoesNotContain("0.2", webhook.Payload);
    }

    [PostgresFact]
    public async Task Webhook_event_is_materialized_once_and_can_be_read_by_its_id()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var eventId = Guid.NewGuid();
        var message = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 4,
            InputRevision = 3,
            CalculatedRevision = 3,
            TraceId = "trace-webhook",
            CorrelationId = "correlation-webhook",
            Execution = new ExecutionOrigin(Guid.NewGuid(), "pricing", Guid.NewGuid(), "pricing", "test-operator", "trace-webhook", "correlation-webhook"),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var envelope = new EventEnvelope
        {
            MessageId = message.EventId,
            EventName = message.EventName,
            Payload = serializer.Serialize(message),
            OccurredAt = message.OccurredAt,
        };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);

        Assert.True(await processor.HandleAsync(envelope));
        Assert.True(await processor.HandleAsync(envelope));

        var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingWebhook(message.EventId));
        Assert.True(status.IsSuccess);
        Assert.Equal("Pending", status.Value.State);
        Assert.Equal(message.ItemId, status.Value.ItemId);
        Assert.Equal(0, status.Value.AttemptCount);
        Assert.Equal(message.Execution, status.Value.Execution);
    }

    [PostgresFact]
    public async Task Successful_delivery_uses_one_stable_idempotency_key_and_is_not_sent_again()
    {
        var receiver = new RecordingReceiver();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        var journalBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        journalBuilder.Configuration["OperationJournal:Storage:Provider"] = "Memory";
        services.AddOperationJournalModule(journalBuilder.Configuration, journalBuilder.Environment, "pricing");
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        services.AddSingleton<HttpClient>(new HttpClient(receiver));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var eventId = Guid.NewGuid();
        var message = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 4,
            InputRevision = 3,
            CalculatedRevision = 3,
            TraceId = "trace-delivery",
            CorrelationId = "correlation-delivery",
            Execution = new ExecutionOrigin(Guid.NewGuid(), "pricing", Guid.NewGuid(), "pricing", "test-operator", "trace-delivery", "correlation-delivery"),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var envelope = new EventEnvelope
        {
            MessageId = eventId,
            EventName = message.EventName,
            Payload = serializer.Serialize(message),
            OccurredAt = message.OccurredAt,
        };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);
        Assert.True(await processor.HandleAsync(envelope));

        var runner = scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>();
        Assert.Equal(1, await runner.DeliverPendingAsync());
        Assert.Equal(0, await runner.DeliverPendingAsync());
        Assert.Equal(eventId, Assert.Single(receiver.Keys));
        Assert.Equal(eventId.ToString("D"), receiver.IdempotencyKey);
        Assert.Equal("trace-delivery", receiver.TraceId);

        var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingWebhook(eventId));
        Assert.True(status.IsSuccess);
        Assert.Equal("Succeeded", status.Value.State);
        Assert.Equal(1, status.Value.AttemptCount);

        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observed = (await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow))
            .Select(entry => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(entry.Payload)).ToArray();
        var finished = Assert.Single(observed, item => item.Phase == "finished");
        Assert.Equal("completed", finished.Outcome);
        Assert.Equal("pricing.webhook.deliver", finished.Metadata!.Action);
        Assert.Equal(message.Execution!.RootOperationId, finished.Metadata.RootOperationId);
        Assert.Equal(message.Execution.CorrelationId, finished.Metadata.CorrelationId);
    }

    [PostgresFact]
    public async Task Invalid_ack_is_failed_and_does_not_retry()
    {
        var sender = new FixedWebhookSender(new PricingWebhookSendResult(false, false, 202, "invalid_ack"));
        var (provider, eventId) = await CreatePendingAsync(sender);
        await using (provider)
        await using (var scope = provider.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>();
            Assert.Equal(0, await runner.DeliverPendingAsync());
            var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPricingWebhook(eventId));
            Assert.Equal("Failed", status.Value.State);
            Assert.Equal("invalid_ack", status.Value.LastError);
        }
    }

    [PostgresFact]
    public async Task Http_sender_classifies_retryable_permanent_and_timeout_responses()
    {
        var cases = new[]
        {
            (Status: HttpStatusCode.TooManyRequests, Retryable: true),
            (Status: HttpStatusCode.InternalServerError, Retryable: true),
            (Status: HttpStatusCode.BadRequest, Retryable: false),
        };
        foreach (var testCase in cases)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNexusStackApplication();
            services.AddPricingPostgres(database.ConnectionString,
                webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
            services.AddSingleton<HttpClient>(new HttpClient(new StatusHandler(testCase.Status)));
            await using var provider = services.BuildServiceProvider();
            var sender = provider.GetRequiredService<IPricingWebhookSender>();
            var result = await sender.SendAsync(new(new Uri("https://receiver.test/webhooks/pricing"), "{}", Guid.NewGuid(), "trace"));
            Assert.False(result.Succeeded);
            Assert.Equal(testCase.Retryable, result.Retryable);
            Assert.Equal((int)testCase.Status, result.StatusCode);
        }

        var timeoutServices = new ServiceCollection();
        timeoutServices.AddLogging();
        timeoutServices.AddNexusStackApplication();
        timeoutServices.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        timeoutServices.AddSingleton<HttpClient>(new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(20) });
        await using var timeoutProvider = timeoutServices.BuildServiceProvider();
        var timeoutSender = timeoutProvider.GetRequiredService<IPricingWebhookSender>();
        var timeoutResult = await timeoutSender.SendAsync(new(new Uri("https://receiver.test/webhooks/pricing"), "{}", Guid.NewGuid(), "trace"));
        Assert.False(timeoutResult.Succeeded);
        Assert.True(timeoutResult.Retryable);
    }

    [PostgresFact]
    public async Task Canceled_send_is_unknown_and_can_be_manually_retried()
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new CancelingWebhookSender(cancellation);
        var (provider, eventId) = await CreatePendingAsync(sender);
        await using (provider)
        await using (var scope = provider.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IPricingWebhookDeliveryRunner>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.DeliverPendingAsync(cancellation.Token));
            var senderApi = scope.ServiceProvider.GetRequiredService<ISender>();
            var unknown = await senderApi.QueryAsync(new GetPricingWebhook(eventId));
            Assert.Equal("Unknown", unknown.Value.State);
            Assert.Equal("canceled", unknown.Value.LastError);
            var retried = await senderApi.SendAsync(new RetryPricingWebhook(eventId, 1));
            Assert.True(retried.IsSuccess);
            Assert.Equal("Pending", retried.Value.State);
        }
    }

    private async Task<(ServiceProvider Provider, Guid EventId)> CreatePendingAsync(IPricingWebhookSender sender)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString,
            webhookOptions: new PricingWebhookOptions { Enabled = true, Endpoint = "https://receiver.test/webhooks/pricing" });
        services.AddSingleton(sender);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var eventId = Guid.NewGuid();
        var message = new PricingRecalculationCompletedV1
        {
            EventId = eventId,
            IdempotencyKey = eventId,
            ItemId = Guid.NewGuid(),
            Version = 1,
            InputRevision = 1,
            CalculatedRevision = 1,
            TraceId = "trace-failure",
            CorrelationId = "correlation-failure",
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var envelope = new EventEnvelope { MessageId = eventId, EventName = message.EventName, Payload = serializer.Serialize(message), OccurredAt = message.OccurredAt };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(PricingRecalculationCompletedV1.Name);
        Assert.True(await processor.HandleAsync(envelope));
        return (provider, eventId);
    }

    private sealed class RecordingReceiver : HttpMessageHandler
    {
        public List<Guid> Keys { get; } = [];
        public string? IdempotencyKey { get; private set; }
        public string? TraceId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            TraceId = request.Headers.GetValues("X-NexusStack-Trace").Single();
            var payload = await request.Content!.ReadFromJsonAsync<PricingRecalculationCompletedV1>(cancellationToken);
            Keys.Add(payload!.EventId);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new PricingWebhookAcceptedV1
                {
                    EventId = payload.EventId,
                    IdempotencyKey = payload.IdempotencyKey,
                    Accepted = true,
                }),
            };
        }
    }

    private sealed class FixedWebhookSender(PricingWebhookSendResult result) : IPricingWebhookSender
    {
        public Task<PricingWebhookSendResult> SendAsync(PricingWebhookSendRequest request, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class CancelingWebhookSender(CancellationTokenSource cancellation) : IPricingWebhookSender
    {
        public Task<PricingWebhookSendResult> SendAsync(PricingWebhookSendRequest request, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<PricingWebhookSendResult>(new OperationCanceledException(cancellation.Token));
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
