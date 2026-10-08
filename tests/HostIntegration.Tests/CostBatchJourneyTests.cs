using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class CostBatchJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task RealGateway_BatchCrashAndBrokerOutage_ResumeOnlyUncommittedRows_AndReachPricing()
    {
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-batch", ClientName = prefix };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var tap = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-tap" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, tap]);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix + "-auditing");
        settings["Costing__Scheduling__Enabled"] = "false";
        settings["Costing__Messaging__Enabled"] = "true";
        settings["Costing__Batches__SegmentSize"] = "4";
        settings["Pricing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__ConsumerName"] = subscription.ConsumerName;
        var offline = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["RabbitMQ__HostName"] = "127.0.0.1",
            ["RabbitMQ__Port"] = "1",
        };
        var routePath = Path.Combine(Path.GetTempPath(), "nsn-batch-routes-" + Guid.NewGuid().ToString("N") + ".json");
        var batchId = Guid.NewGuid();
        var firstItem = Guid.NewGuid();
        var lastItem = Guid.NewGuid();
        var conflictItem = Guid.NewGuid();
        var input = new
        {
            batchRequestId = batchId,
            rows = new[]
            {
                new { sourceRow = 10, itemId = firstItem, expectedVersion = "0", purchaseCost = 1m, freightCost = 2m },
                new { sourceRow = 20, itemId = firstItem, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m },
                new { sourceRow = 30, itemId = conflictItem, expectedVersion = "1", purchaseCost = 10m, freightCost = 20m },
                new { sourceRow = 40, itemId = lastItem, expectedVersion = "0", purchaseCost = 10m, freightCost = 20m },
            },
        };
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString, worker: true, settings: settings);
            pricing.Authenticate();
            using var priced = await pricing.Client.PostAsJsonAsync(Relative("/api/pricing/cost"), new { requestId = Guid.NewGuid(), itemId = firstItem, expectedVersion = "0", cost = 200m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, priced.StatusCode);
            await WaitDataAsync(pricing.Client, $"/api/pricing/items/{firstItem}", x => x.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number);
            await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(costs.ConnectionString) { Pooling = false }.ConnectionString);
            await barrier.OpenAsync();
            await using (var install = new NpgsqlCommand("""
                SELECT pg_advisory_lock(500050);
                CREATE FUNCTION costing.pause_batch_tail() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW."Sequence" = 4 THEN PERFORM pg_advisory_xact_lock(500050); END IF; RETURN NEW; END $$;
                CREATE TRIGGER pause_batch_tail BEFORE UPDATE ON costing.batch_rows FOR EACH ROW EXECUTE FUNCTION costing.pause_batch_tail();
                """, barrier)) { await install.ExecuteNonQueryAsync(); }
            int interruptedBackend;
            await using (var first = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, worker: true, settings: offline))
            {
                first.Authenticate();
                using var businessReady = await first.Client.GetAsync(Relative("/health/ready"));
                Assert.Equal(HttpStatusCode.OK, businessReady.StatusCode);
                using var deliveryFault = await first.Client.GetAsync(Relative("/health/delivery"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, deliveryFault.StatusCode);
                await WriteRoutesAsync(routePath, first.Client.BaseAddress!, pricing.Client.BaseAddress!);
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath);
                using var anonymous = await gateway.Client.GetAsync(Relative("/api/costing/batches"));
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                gateway.Authenticate(root: false);
                using var forbidden = await gateway.Client.PostAsJsonAsync(Relative("/api/costing/batches"), input);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                foreach (var path in new[] { "/api/costing/batches", $"/api/costing/batches/{batchId}",
                    $"/api/costing/batches/{batchId}/rows", $"/api/costing/batches/{batchId}/attempts" })
                {
                    using var denied = await gateway.Client.GetAsync(Relative(path));
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
                foreach (var action in new[] { "cancel", "retry" })
                {
                    using var denied = await gateway.Client.PostAsJsonAsync(Relative($"/api/costing/batches/{batchId}/{action}"), new { expectedEpoch = "0" });
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
                gateway.Authenticate();
                var invalidId = Guid.NewGuid();
                using var invalid = await gateway.Client.PostAsJsonAsync(Relative("/api/costing/batches"), new
                {
                    batchRequestId = invalidId,
                    rows = new[] { new { sourceRow = 99, itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = -1m, freightCost = 20m } },
                });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                var validation = await invalid.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(1, validation.GetProperty("errorCount").GetInt32());
                Assert.Equal(99, validation.GetProperty("errors")[0].GetProperty("sourceRow").GetInt32());
                using var limited = await gateway.Client.GetAsync(Relative("/api/costing/batches?limit=201"));
                Assert.Equal(HttpStatusCode.BadRequest, limited.StatusCode);
                using var oversized = new HttpRequestMessage(HttpMethod.Post, Relative("/api/costing/batches"));
                oversized.Headers.TransferEncodingChunked = true;
                oversized.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024 + 1))));
                oversized.Content.Headers.ContentType = new("application/json");
                using var tooLarge = await gateway.Client.SendAsync(oversized);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
                using var accepted = await gateway.Client.PostAsJsonAsync(Relative("/api/costing/batches"), input);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                interruptedBackend = await WaitForBlockedAsync(barrier);
                // End both real processes while the last row is blocked, before releasing the barrier.
            }
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(500050)", barrier)) { await release.ExecuteNonQueryAsync(); }
            await WaitForExitAsync(barrier, interruptedBackend);
            await using (var uninstall = new NpgsqlCommand("DROP TRIGGER pause_batch_tail ON costing.batch_rows; DROP FUNCTION costing.pause_batch_tail()", barrier)) { await uninstall.ExecuteNonQueryAsync(); }
            await using (var inspect = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: offline))
            {
                inspect.Authenticate();
                var batch = await WaitDataAsync(inspect.Client, $"/api/costing/batches/{batchId}", _ => true);
                Assert.Equal(3, batch.GetProperty("checkpoint").GetInt32());
                Assert.Equal(1, batch.GetProperty("imported").GetInt32());
                var localCost = await WaitDataAsync(inspect.Client, $"/api/costing/items/{firstItem}", _ => true);
                Assert.Equal(80m, localCost.GetProperty("purchaseCost").GetDecimal());
                var pendingFacts = await WaitDataAsync(inspect.Client, "/api/costing/audit-deliveries?state=Pending&limit=100", _ => true);
                var stoppedFacts = await WaitDataAsync(inspect.Client, "/api/costing/audit-deliveries?state=DeadLettered&limit=100", _ => true);
                Assert.True(pendingFacts.GetArrayLength() + stoppedFacts.GetArrayLength() > 0);
                using var rolledBack = await inspect.Client.GetAsync(Relative($"/api/costing/items/{lastItem}"));
                Assert.Equal(HttpStatusCode.NotFound, rolledBack.StatusCode);
                var rows = await WaitDataAsync(inspect.Client, $"/api/costing/batches/{batchId}/rows", _ => true);
                var childId = rows[1].GetProperty("taskId").GetGuid();
                using var delivery = await inspect.Client.GetAsync(Relative($"/api/costing/tasks/{childId}/delivery"));
                if (delivery.StatusCode == HttpStatusCode.OK)
                {
                    Assert.NotEqual("Delivered", (await delivery.Content.ReadApiDataAsync()).GetProperty("state").GetString());
                }
                else { Assert.Equal(HttpStatusCode.NotFound, delivery.StatusCode); }
            }
            await using var recovered = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString,
                worker: true, settings: settings);
            await WriteRoutesAsync(routePath, recovered.Client.BaseAddress!, pricing.Client.BaseAddress!);
            await using var restoredGateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath);
            restoredGateway.Authenticate();
            var completed = await WaitDataAsync(restoredGateway.Client, $"/api/costing/batches/{batchId}", x => x.GetProperty("state").GetString() == "CompletedWithErrors");
            Assert.Equal(4, completed.GetProperty("checkpoint").GetInt32());
            Assert.Equal(2, completed.GetProperty("imported").GetInt32());
            Assert.Equal(1, completed.GetProperty("rejected").GetInt32());
            Assert.Equal(JsonValueKind.String, completed.GetProperty("epoch").ValueKind);
            using var rowPage = await restoredGateway.Client.GetAsync(Relative($"/api/costing/batches/{batchId}/rows"));
            Assert.Equal("4", (await rowPage.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetString());
            var result = await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{firstItem}", x => x.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number
                && x.GetProperty("breakEvenPrice").GetDecimal() == 125m);
            Assert.Equal(100m, result.GetProperty("cost").GetDecimal());
            await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{lastItem}", x => x.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number
                && x.GetProperty("breakEvenPrice").GetDecimal() == 30m, allowMissing: true);
            var original = await AuditBusinessJourneyTests.ReadEnvelopeAsync(broker, tap.QueueName, CostCalculatedV1.Name);
            var originalItem = new SystemTextJsonIntegrationEventSerializer().Deserialize<CostCalculatedV1>(original.Payload).ItemId;
            var originalPrice = await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{originalItem}", _ => true);
            await using var bus = new RabbitMqEventBus(broker);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            var barrierItem = Guid.NewGuid();
            var barrierEvent = new CostCalculatedV1 { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, ItemId = barrierItem, CostRevision = 1, UnitCost = 5m };
            Assert.True((await bus.PublishAsync(new EventEnvelope
            {
                MessageId = barrierEvent.EventId,
                EventName = barrierEvent.EventName,
                OccurredAt = barrierEvent.OccurredAt,
                Payload = new SystemTextJsonIntegrationEventSerializer().Serialize(barrierEvent),
            })).IsSuccess);
            await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{barrierItem}", x => x.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number, allowMissing: true);
            var afterDuplicate = await WaitDataAsync(restoredGateway.Client, $"/api/pricing/items/{originalItem}", _ => true);
            Assert.Equal(originalPrice.GetProperty("version").GetString(), afterDuplicate.GetProperty("version").GetString());
            using var replay = await restoredGateway.Client.PostAsJsonAsync(Relative("/api/costing/batches"), input);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal(4, (await replay.Content.ReadApiDataAsync()).GetProperty("checkpoint").GetInt32());
        }
        finally
        {
            File.Delete(routePath);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task WriteRoutesAsync(string path, Uri costing, Uri pricing) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
    {
        routes = new[]
        {
            new { routeId = "costing", clusterId = "costing", path = "/api/costing/{**catch-all}", requireAuthentication = true },
            new { routeId = "pricing", clusterId = "pricing", path = "/api/pricing/{**catch-all}", requireAuthentication = true },
        },
        clusters = new[]
        {
            new { clusterId = "costing", destinations = new[] { new { name = "primary", address = costing.ToString() } } },
            new { clusterId = "pricing", destinations = new[] { new { name = "primary", address = pricing.ToString() } } },
        },
    }));

    private static async Task<JsonElement> WaitDataAsync(HttpClient client, string path, Func<JsonElement, bool> predicate, bool allowMissing = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (true)
        {
            using var response = await client.GetAsync(Relative(path), timeout.Token);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) { await Task.Delay(100, timeout.Token); continue; }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await response.Content.ReadApiDataAsync();
            if (predicate(data)) { return data; }
            await Task.Delay(100, timeout.Token);
        }
    }
    private static async Task<int> WaitForBlockedAsync(NpgsqlConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)) LIMIT 1", connection);
            command.Parameters.AddWithValue("barrier", connection.ProcessID);
            if (await command.ExecuteScalarAsync(timeout.Token) is int backend) { return backend; }
            await Task.Delay(20, timeout.Token);
        }
    }
    private static async Task WaitForExitAsync(NpgsqlConnection connection, int backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pid = @backend)", connection);
            command.Parameters.AddWithValue("backend", backend);
            if (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(50, timeout.Token);
        }
    }
    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
