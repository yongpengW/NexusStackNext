using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class TaskOperationJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task KilledAttempt_RemainsUnconfirmed_AfterJournalRecoveryAndSuccessfulNewEpoch()
    {
        await using var central = await databases.CreateAsync();
        await using var source = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(source.ConnectionString);
        await MigrateJournalAsync(typeof(PricingHostMarker).Assembly.Location, source.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-interrupted-operation", ClientName = prefix };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var topology = EventTopology.Create(broker.ExchangeName, [facts, operations]);
        var settings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
        settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
        var sourceSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = source.ConnectionString,
            ["Pricing__Messaging__Enabled"] = "false",
        };
        var requestId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        OperationObservedV1 interrupted;
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var storage = new ServiceCollection().AddOperationJournalPostgresStorage(source.ConnectionString).BuildServiceProvider();
            await using var scope = storage.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(source.ConnectionString) { Pooling = false }.ConnectionString);
            await barrier.OpenAsync();
            await using (var install = new NpgsqlCommand("""
                SELECT pg_advisory_lock(630063);
                CREATE FUNCTION pricing.pause_operation_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(630063); RETURN NEW; END $$;
                CREATE TRIGGER pause_operation_commit BEFORE UPDATE ON pricing.quotes
                FOR EACH ROW EXECUTE FUNCTION pricing.pause_operation_commit();
                """, barrier))
            {
                await install.ExecuteNonQueryAsync();
            }
            int blockedBackend;
            var offlineSettings = new Dictionary<string, string>(sourceSettings, StringComparer.Ordinal) { ["RabbitMQ__HostName"] = string.Empty };
            await using (var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing",
                source.ConnectionString, worker: true, leaseDuration: TimeSpan.FromSeconds(5), settings: offlineSettings))
            {
                first.Authenticate();
                using var accepted = await first.Client.PostAsJsonAsync(Relative("/api/pricing/cost"),
                    new { requestId, itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                blockedBackend = await WaitForBlockedBackendAsync(barrier);
                interrupted = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal),
                    item => item.Kind == "task" && item.Metadata!.TaskId == requestId);
                Assert.Equal("started", interrupted.Phase);
                Assert.Equal(1, interrupted.Metadata!.TaskEpoch);
                // Dispose 直接杀掉进程：不发送取消令牌，也不给 finally 生成 Finished 的机会。
            }
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(630063)", barrier))
            {
                Assert.True((bool)(await release.ExecuteScalarAsync())!);
            }
            await WaitForBackendExitAsync(barrier, blockedBackend);
            await using (var recover = new NpgsqlCommand("""
                DROP TRIGGER pause_operation_commit ON pricing.quotes;
                DROP FUNCTION pricing.pause_operation_commit();
                """, barrier))
            {
                await recover.ExecuteNonQueryAsync();
            }
            Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.OperationId == interrupted.OperationId);
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "interrupted-operation-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "interrupted-operation-root");
            await using (var deliveryOnly = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing",
                source.ConnectionString, settings: sourceSettings))
            {
                deliveryOnly.Authenticate();
                using var quote = await deliveryOnly.Client.GetAsync(Relative($"/api/pricing/items/{itemId}"));
                Assert.Equal(JsonValueKind.Null, (await quote.Content.ReadApiDataAsync()).GetProperty("breakEvenPrice").ValueKind);
                using var task = await deliveryOnly.Client.GetAsync(Relative($"/api/pricing/tasks/{requestId}"));
                Assert.Equal("Running", (await task.Content.ReadApiDataAsync()).GetProperty("state").GetString());
                var unfinished = await WaitForDataAsync(reader.Client, $"/api/auditing/operations?operationId={interrupted.OperationId}", data => data.GetArrayLength() == 1);
                Assert.Equal("unconfirmed", unfinished[0].GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, unfinished[0].GetProperty("finishedAt").ValueKind);
            }
            await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing",
                source.ConnectionString, worker: true, settings: sourceSettings);
            restarted.Authenticate();
            var completed = await WaitForDataAsync(restarted.Client, $"/api/pricing/tasks/{requestId}", data => data.GetProperty("state").GetString() == "Succeeded");
            Assert.Equal(2, completed.GetProperty("epoch").ReadHttpInt64());
            Assert.Equal(new[] { "Expired", "Succeeded" }, completed.GetProperty("history").EnumerateArray().Select(item => item.GetProperty("outcome").GetString()));
            var observed = await WaitForDataAsync(reader.Client, $"/api/auditing/operations?traceId={interrupted.TraceId}", data =>
                data.EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "task" && item.GetProperty("outcome").GetString() == "completed"));
            var attempts = observed.EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "task").ToArray();
            Assert.Equal(2, attempts.Length);
            var unknown = Assert.Single(attempts, item => item.GetProperty("operationId").GetGuid() == interrupted.OperationId);
            Assert.Equal("unconfirmed", unknown.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, unknown.GetProperty("finishedAt").ValueKind);
            var succeeded = Assert.Single(attempts, item => item.GetProperty("outcome").GetString() == "completed");
            Assert.NotEqual(interrupted.OperationId, succeeded.GetProperty("operationId").GetGuid());
            Assert.Equal(2, succeeded.GetProperty("metadata").GetProperty("taskEpoch").ReadHttpInt64());
            foreach (var attempt in attempts)
            {
                Assert.Equal(JsonValueKind.Null, attempt.GetProperty("actorId").ValueKind);
                var metadata = attempt.GetProperty("metadata");
                Assert.Equal(requestId, metadata.GetProperty("taskId").GetGuid());
                Assert.Equal(interrupted.Metadata!.RootOperationId, metadata.GetProperty("rootOperationId").GetGuid());
                Assert.Equal("test-operator", metadata.GetProperty("initiatorId").GetString());
            }
            using var result = await restarted.Client.GetAsync(Relative($"/api/pricing/items/{itemId}"));
            Assert.Equal(100m, (await result.Content.ReadApiDataAsync()).GetProperty("breakEvenPrice").GetDecimal());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task GatewayAcceptance_SurvivesRestart_AndLinksCostingAndPricingAttemptsThroughRealBroker()
    {
        await using var central = await databases.CreateAsync();
        await using var costDatabase = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(costDatabase.ConnectionString);
        await MigrateJournalAsync(typeof(CostingHostMarker).Assembly.Location, costDatabase.ConnectionString);
        await using var priceDatabase = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(priceDatabase.ConnectionString);
        await MigrateJournalAsync(typeof(PricingHostMarker).Assembly.Location, priceDatabase.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-task-operations", ClientName = prefix };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var costs = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [facts, operations, costs]);
        var routePath = Path.Combine(Path.GetTempPath(), $"nsn-task-operation-routes-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
            settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "task-operation-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "task-operation-root");
            var priceSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                ["OperationJournal__Storage__Provider"] = "Postgres",
                ["ConnectionStrings__OperationJournal"] = priceDatabase.ConnectionString,
                ["Pricing__Messaging__Enabled"] = "true",
                ["Pricing__Messaging__ConsumerName"] = costs.ConsumerName,
            };
            await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location,
                "Pricing", priceDatabase.ConnectionString, worker: true, settings: priceSettings);
            var costSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                ["OperationJournal__Storage__Provider"] = "Postgres",
                ["ConnectionStrings__OperationJournal"] = costDatabase.ConnectionString,
                ["Costing__Messaging__Enabled"] = "true",
                ["Costing__Scheduling__Enabled"] = "false",
            };
            var offlineSettings = new Dictionary<string, string>(costSettings, StringComparer.Ordinal)
            {
                ["RabbitMQ__HostName"] = string.Empty,
                ["Costing__Messaging__Enabled"] = "false",
            };
            var requestId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var traceId = Guid.NewGuid().ToString("N");
            Guid rootOperation;
            await using (var producer = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
                "Costing", costDatabase.ConnectionString, settings: offlineSettings))
            {
                await File.WriteAllTextAsync(routePath, JsonSerializer.Serialize(new
                {
                    routes = new[] { new { routeId = "costing", clusterId = "costing", path = "/api/costing/{**catch-all}", requireAuthentication = true } },
                    clusters = new[] { new { clusterId = "costing", destinations = new[] { new { name = "primary", address = producer.Client.BaseAddress!.ToString() } } } },
                }));
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath, offlineSettings);
                gateway.Authenticate();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/costing/cost")
                {
                    Content = JsonContent.Create(new { requestId, itemId, expectedVersion = 0, purchaseCost = 72m, freightCost = 8m }),
                };
                request.Headers.Add("traceparent", $"00-{traceId}-1234567890abcdef-01");
                request.Headers.Add("X-Correlation-Id", "task-chain-63");
                using var accepted = await gateway.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                Assert.Equal(requestId, (await accepted.Content.ReadApiDataAsync()).GetProperty("taskId").GetGuid());
                await using var storage = new ServiceCollection().AddOperationJournalPostgresStorage(costDatabase.ConnectionString).BuildServiceProvider();
                await using var scope = storage.CreateAsyncScope();
                var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
                await GatewayResilienceTests.EventuallyAsync(async () => (await OperationEndpointInventoryTests.ReadAsync(journal))
                    .Count(item => item.TraceId == traceId && item.Phase == "finished") == 2);
                rootOperation = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal),
                    item => item.TraceId == traceId && item.Source == "costing" && item.Phase == "finished").OperationId;
                using var absent = await reader.Client.GetAsync(Relative($"/api/auditing/operations?traceId={traceId}"));
                Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
                Assert.Empty((await absent.Content.ReadApiDataAsync()).EnumerateArray());
            }
            // 原请求与网关均退出后才执行任务、恢复 journal 交付；来源关联必须来自持久记录。
            await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
                "Costing", costDatabase.ConnectionString, worker: true, settings: costSettings);
            pricing.Authenticate();
            await WaitForDataAsync(pricing.Client, $"/api/pricing/items/{itemId}", data =>
                data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number && data.GetProperty("breakEvenPrice").GetDecimal() == 80m, allowNotFound: true);
            var observations = await WaitForDataAsync(reader.Client, $"/api/auditing/operations?traceId={traceId}", data =>
                data.GetArrayLength() == 5 && data.EnumerateArray().All(item => item.GetProperty("finishedAt").ValueKind != JsonValueKind.Null));
            var items = observations.EnumerateArray().ToArray();
            Assert.Equal(5, items.Select(item => item.GetProperty("operationId").GetGuid()).Distinct().Count());
            Assert.Equal(2, items.Count(item => item.GetProperty("kind").GetString() == "http"));
            var acceptedCost = Assert.Single(items, item => item.GetProperty("operationId").GetGuid() == rootOperation);
            Assert.Equal("accepted", acceptedCost.GetProperty("outcome").GetString());
            Assert.Equal("test-operator", acceptedCost.GetProperty("actorId").GetString());
            var attempts = items.Where(item => item.GetProperty("kind").GetString() == "task").ToArray();
            Assert.Equal(2, attempts.Length);
            var costAttempt = Assert.Single(attempts, item => item.GetProperty("source").GetString() == "costing");
            var priceAttempt = Assert.Single(attempts, item => item.GetProperty("source").GetString() == "pricing");
            Assert.Equal(rootOperation, costAttempt.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            var consumption = Assert.Single(items, item => item.GetProperty("kind").GetString() == "message");
            Assert.Equal("accepted", consumption.GetProperty("outcome").GetString());
            Assert.Equal("pricing", consumption.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, consumption.GetProperty("actorId").ValueKind);
            Assert.Equal(CostCalculatedV1.Name, consumption.GetProperty("metadata").GetProperty("subjectType").GetString());
            Assert.Equal(requestId.ToString("D"), consumption.GetProperty("metadata").GetProperty("subjectId").GetString());
            Assert.Equal(costAttempt.GetProperty("operationId").GetGuid(), consumption.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            Assert.Equal(consumption.GetProperty("operationId").GetGuid(), priceAttempt.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            foreach (var attempt in attempts)
            {
                Assert.Equal("completed", attempt.GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, attempt.GetProperty("actorId").ValueKind);
                Assert.NotEqual(JsonValueKind.Null, attempt.GetProperty("startedAt").ValueKind);
                var metadata = attempt.GetProperty("metadata");
                Assert.Equal("test-operator", metadata.GetProperty("initiatorId").GetString());
                Assert.Equal(rootOperation, metadata.GetProperty("rootOperationId").GetGuid());
                Assert.Equal("costing", metadata.GetProperty("rootSource").GetString());
                Assert.Equal(attempt.GetProperty("source").GetString(), metadata.GetProperty("parentSource").GetString());
                Assert.Equal(requestId, metadata.GetProperty("taskId").GetGuid());
                Assert.Equal(1, metadata.GetProperty("taskEpoch").ReadHttpInt64());
                Assert.Equal("task-chain-63", metadata.GetProperty("correlationId").GetString());
            }
            var committed = await WaitForDataAsync(reader.Client, $"/api/auditing/entries?source=pricing&subjectId={itemId:D}", data => data.GetArrayLength() == 3);
            var priceFacts = committed.EnumerateArray().Select(item => item.GetProperty("fact")).ToArray();
            Assert.All(priceFacts, fact =>
            {
                Assert.Equal(JsonValueKind.Null, fact.GetProperty("actorId").ValueKind);
                Assert.Equal(rootOperation, fact.GetProperty("execution").GetProperty("rootOperationId").GetGuid());
                Assert.Equal("test-operator", fact.GetProperty("execution").GetProperty("initiatorId").GetString());
                Assert.Equal("task-chain-63", fact.GetProperty("correlationId").GetString());
                Assert.DoesNotContain("feeRate", fact.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("breakEvenPrice", fact.GetRawText(), StringComparison.Ordinal);
            });
            var result = Assert.Single(priceFacts, fact => fact.GetProperty("action").GetString() == "pricing.price-quote.result-applied");
            Assert.Equal(3, result.GetProperty("subjectVersion").ReadHttpInt64());
            Assert.Equal(priceAttempt.GetProperty("operationId").GetGuid(), result.GetProperty("execution").GetProperty("operationId").GetGuid());
            var imported = Assert.Single(priceFacts, fact => fact.GetProperty("action").GetString() == "pricing.price-quote.costing-applied");
            Assert.Equal("costing", imported.GetProperty("relatedSubject").GetProperty("context").GetString());
            Assert.Equal("cost-sheet", imported.GetProperty("relatedSubject").GetProperty("type").GetString());
            Assert.Equal(itemId.ToString("D"), imported.GetProperty("relatedSubject").GetProperty("id").GetString());
            Assert.Equal(consumption.GetProperty("operationId").GetGuid(), imported.GetProperty("execution").GetProperty("operationId").GetGuid());
            Assert.Single(priceFacts, fact => fact.GetProperty("action").GetString() == "pricing.price-quote.created");
        }
        finally
        {
            File.Delete(routePath);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    internal static async Task MigrateJournalAsync(string assembly, string connection)
    {
        var start = BusinessProcess.StartInfo(assembly, "OperationJournal", connection);
        start.ArgumentList.Add("migrate-operation-journal");
        Assert.Equal(0, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
    }

    private static async Task<int> WaitForBlockedBackendAsync(NpgsqlConnection barrier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var blocked = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)) LIMIT 1", barrier);
            blocked.Parameters.AddWithValue("barrier", barrier.ProcessID);
            if (await blocked.ExecuteScalarAsync(timeout.Token) is int backend) { return backend; }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WaitForBackendExitAsync(NpgsqlConnection observer, int backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var remaining = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pid = @backend)", observer);
            remaining.Parameters.AddWithValue("backend", backend);
            if (!(bool)(await remaining.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task<JsonElement> WaitForDataAsync(HttpClient client, string path, Func<JsonElement, bool> predicate, bool allowNotFound = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(Relative(path), timeout.Token);
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                {
                    await Task.Delay(100, timeout.Token);
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var data = await response.Content.ReadApiDataAsync();
                if (predicate(data)) { return data; }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail("The authenticated business or operation query did not expose the expected durable task chain.");
            throw;
        }
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
