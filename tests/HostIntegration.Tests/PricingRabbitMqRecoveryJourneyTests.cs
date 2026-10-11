using System.Net;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PricingRabbitMqRecoveryJourneyTests
{
    [AuditBrokerFact]
    public async Task Pricing_consumer_reconnects_after_runtime_rabbitmq_disconnect()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await MigratePricingAsync(database.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-runtime", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        await using var proxy = new RabbitMqFaultProxy(broker);
        var settings = AuditBusinessJourneyTests.Settings(proxy.Options, subscription.ConsumerName);
        settings["Pricing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__ConsumerName"] = subscription.ConsumerName;
        settings["Pricing__Webhook__Enabled"] = "false";
        settings["Pricing__Worker__Enabled"] = "true";
        BusinessProcess? pricing = null;
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString,
                worker: true, settings: settings);
            pricing.Authenticate();
            var firstItem = Guid.NewGuid();
            await PublishAsync(broker, new CostCalculatedV1 { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, ItemId = firstItem, CostRevision = 1, UnitCost = 10m });
            var firstQuote = await WaitQuoteAsync(pricing.Client, firstItem);
            await proxy.WaitForConnectionAsync(TimeSpan.FromSeconds(10));

            proxy.SetOffline(true);
            await proxy.WaitForDisconnectedAsync(TimeSpan.FromSeconds(10));
            var secondItem = Guid.NewGuid();
            await PublishAsync(broker, new CostCalculatedV1 { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, ItemId = secondItem, CostRevision = 2, UnitCost = 25m });
            await Task.Delay(TimeSpan.FromSeconds(3));
            using (var unavailable = await pricing.Client.GetAsync(new Uri($"/api/pricing/items/{secondItem}", UriKind.Relative)))
            {
                Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            }
            proxy.SetOffline(false);

            var recovered = await WaitQuoteAsync(pricing.Client, secondItem);
            Assert.Equal(25m, recovered.GetProperty("cost").GetDecimal());
            Assert.NotEqual(firstQuote.GetProperty("itemId").GetGuid(), recovered.GetProperty("itemId").GetGuid());
        }
        finally
        {
            if (pricing is not null) { await pricing.DisposeAsync(); }
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task PublishAsync(RabbitMqOptions broker, CostCalculatedV1 message)
    {
        await using var bus = new RabbitMqEventBus(broker);
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        Assert.True((await bus.PublishAsync(new EventEnvelope
        {
            MessageId = message.EventId,
            EventName = message.EventName,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = serializer.Serialize(message),
        })).IsSuccess);
    }

    private static async Task<JsonElement> WaitQuoteAsync(HttpClient client, Guid itemId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative), timeout.Token);
            if (response.IsSuccessStatusCode) { return await response.Content.ReadApiDataAsync(); }
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable,
                $"Unexpected HTTP {(int)response.StatusCode} while waiting for quote {itemId:D}.");
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task MigratePricingAsync(string connection)
    {
        await using var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true);
        var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", connection);
        start.ArgumentList.Add("migrate-pricing");
        Assert.Equal(0, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
    }
}
