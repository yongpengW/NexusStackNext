using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.PricingHost;
using RabbitMQ.Client;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed partial class PricingFactCapacityTests
{
    [PricingBrokerPostgresFact]
    public async Task CapacityRefusal_RetainsOriginalInDeadLetters_AndRedriveAfterHostRestartAcceptsOnce()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-exchange", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await SetQuotaAsync(database.ConnectionString, 1);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["RabbitMq__HostName"] = broker.HostName,
                ["RabbitMq__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RabbitMq__UserName"] = broker.UserName,
                ["RabbitMq__Password"] = broker.Password,
                ["RabbitMq__VirtualHost"] = broker.VirtualHost,
                ["RabbitMq__ExchangeName"] = broker.ExchangeName,
                ["RabbitMq__ClientName"] = broker.ClientName,
                ["Pricing__Messaging__Enabled"] = "true",
                ["Pricing__Messaging__ConsumerName"] = subscription.ConsumerName,
            };
            var item = Guid.NewGuid();
            var message = CostIngestionTests.Cost(item, 1, 100m);
            var factory = new ConnectionFactory
            {
                HostName = broker.HostName,
                Port = broker.Port,
                UserName = broker.UserName,
                Password = broker.Password,
                VirtualHost = broker.VirtualHost,
            };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await using var bus = new RabbitMqEventBus(broker);
            BasicGetResult retained;
            await using (var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
            {
                first.Authenticate();
                Assert.True((await bus.PublishAsync(message)).IsSuccess);
                retained = await WaitForDeadLetterAsync(channel, subscription.DeadLetterQueueName);
                Assert.Equal(message.MessageId.ToString("D"), retained.BasicProperties.MessageId);
                Assert.Equal(message.Payload, Encoding.UTF8.GetString(retained.Body.Span));
                using var quote = await first.Client.GetAsync(new Uri($"/api/pricing/items/{item}", UriKind.Relative));
                using var task = await first.Client.GetAsync(new Uri($"/api/pricing/tasks/{message.MessageId}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NotFound, quote.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, task.StatusCode);
            }
            // 消息仍未确认地由 broker 持有。操作者恢复容量后，以原身份和内容重驱。
            await SetQuotaAsync(database.ConnectionString, 2);
            await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
            restarted.Authenticate();
            Assert.True((await bus.PublishAsync(message)).IsSuccess);
            await channel.BasicAckAsync(retained.DeliveryTag, multiple: false);
            var accepted = await WaitForAcceptedMessageAsync(restarted.Client, message.MessageId);
            Assert.Empty(accepted.GetProperty("history").EnumerateArray());
            using var recovered = await restarted.Client.GetAsync(new Uri($"/api/pricing/items/{item}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var before = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
            Assert.Equal(100m, before.GetProperty("cost").GetDecimal());
            Assert.Equal(1, before.GetProperty("costingRevision").ReadHttpInt64());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("breakEvenPrice").ValueKind);
            // 同一发布连接保持顺序：随后冲突消息到达死信，证明前面的重复消息已经消费。
            Assert.True((await bus.PublishAsync(message)).IsSuccess);
            var serializer = new SystemTextJsonIntegrationEventSerializer();
            var conflict = OutboxEntry.From(serializer.Deserialize<CostCalculatedV1>(message.Payload) with { UnitCost = 101m }, serializer).ToEnvelope();
            Assert.True((await bus.PublishAsync(conflict)).IsSuccess);
            var rejected = await WaitForDeadLetterAsync(channel, subscription.DeadLetterQueueName);
            Assert.Equal(conflict.MessageId.ToString("D"), rejected.BasicProperties.MessageId);
            Assert.Equal(conflict.Payload, Encoding.UTF8.GetString(rejected.Body.Span));
            await channel.BasicAckAsync(rejected.DeliveryTag, multiple: false);
            using var unchanged = await restarted.Client.GetAsync(new Uri($"/api/pricing/items/{item}", UriKind.Relative));
            Assert.Equal(before.GetRawText(), (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
            await using var app = CreateApplication(database.ConnectionString);
            await using var scope = app.CreateAsyncScope();
            // 事实没有中央订阅者，可能已进入投递退避；按未来可投递时间读，不依赖轮询时机。
            var facts = await scope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(10, DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(2, facts.Count);
            Assert.All(facts, fact => Assert.Equal(PriceQuoteCommittedV1.Name, fact.EventName));
        }
        finally
        {
            await database.DisposeAsync();
            var factory = new ConnectionFactory
            {
                HostName = broker.HostName,
                Port = broker.Port,
                UserName = broker.UserName,
                Password = broker.Password,
                VirtualHost = broker.VirtualHost,
            };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in topology.AllQueueNames) { await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); }
            await channel.ExchangeDeleteAsync(topology.ExchangeName);
        }
    }

    private static async Task<BasicGetResult> WaitForDeadLetterAsync(IChannel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false, timeout.Token);
            if (result is not null) { return result; }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<JsonElement> WaitForAcceptedMessageAsync(HttpClient client, Guid taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative), timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var task = (await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("data").Clone();
                Assert.Equal("Pending", task.GetProperty("state").GetString());
                return task;
            }
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await Task.Delay(100, timeout.Token);
        }
    }
}

internal sealed class PricingBrokerPostgresFactAttribute : FactAttribute
{
    public PricingBrokerPostgresFactAttribute()
    {
        if (RabbitMqTestBroker.TryGetOptions() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable)))
        {
            Skip = "该容量恢复旅程需要真实 PostgreSQL 和 RabbitMQ。";
        }
    }
}
