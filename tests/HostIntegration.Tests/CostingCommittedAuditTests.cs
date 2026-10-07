using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class CostingCommittedAuditTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task CostFacts_SurviveProducerRestart_AndLinkSystemCalculationToItsOriginalHttpOperation()
    {
        await using var central = await databases.CreateAsync();
        await using var source = await databases.CreateAsync("costing-only");
        await TaskOperationJourneyTests.MigrateJournalAsync(typeof(CostingHostMarker).Assembly.Location, source.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-cost-facts", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
            new EventSubscription { EventName = CostSheetCommittedV1.Name, ConsumerName = prefix + "-costing" },
            new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-business-result" },
        ]);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        var sourceSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = source.ConnectionString,
            ["Costing__Messaging__Enabled"] = "true",
            ["Costing__Scheduling__Enabled"] = "false",
        };
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var requestId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var offline = new Dictionary<string, string>(sourceSettings, StringComparer.Ordinal)
            {
                ["RabbitMQ__HostName"] = string.Empty,
                ["Costing__Messaging__Enabled"] = "false",
            };
            await using (var producer = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", source.ConnectionString, settings: offline))
            {
                producer.Authenticate();
                producer.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "cost-facts-restart");
                using var accepted = await producer.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), new
                {
                    requestId,
                    itemId,
                    expectedVersion = 0,
                    purchaseCost = 80m,
                    freightCost = 20m,
                    actorId = "forged-operator",
                });
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            await using var consumer = await PlatformHostProcess.StartAsync(central.ConnectionString, "cost-audit-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(consumer.Client, "journey-root", "cost-audit-root-password");
            await using var recovered = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", source.ConnectionString,
                worker: true, settings: sourceSettings);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await consumer.Client.GetAsync(new Uri($"/api/auditing/entries?source=costing&subjectId={itemId:D}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                facts = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(item => item.GetProperty("fact").Clone())
                    .OrderBy(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()).ToArray();
                Assert.True(facts.Length <= 2, "重复交付不能增加成本事实。");
                if (facts.Length == 2) { break; }
                await Task.Delay(100, budget.Token);
            }
            Assert.Equal(new[] { "costing.cost-sheet.created", "costing.cost-sheet.result-applied" }, facts.Select(fact => fact.GetProperty("action").GetString()));
            Assert.Equal(new long[] { 1, 2 }, facts.Select(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()));
            Assert.Equal("test-operator", facts[0].GetProperty("actorId").GetString());
            Assert.Equal(JsonValueKind.Null, facts[1].GetProperty("actorId").ValueKind);
            var creation = facts[0].GetProperty("execution");
            var calculation = facts[1].GetProperty("execution");
            Assert.NotEqual(creation.GetProperty("operationId").GetGuid(), calculation.GetProperty("operationId").GetGuid());
            Assert.Equal(creation.GetProperty("operationId").GetGuid(), calculation.GetProperty("rootOperationId").GetGuid());
            Assert.Equal("test-operator", calculation.GetProperty("initiatorId").GetString());
            Assert.All(facts, fact =>
            {
                Assert.Equal("cost-sheet", fact.GetProperty("subjectType").GetString());
                Assert.Equal("cost-facts-restart", fact.GetProperty("correlationId").GetString());
                Assert.DoesNotContain("purchaseCost", fact.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("unitCost", fact.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("forged-operator", fact.GetRawText(), StringComparison.Ordinal);
            });
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }
}
