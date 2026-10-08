using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationCoverageJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task GatewayCostRequest_RecordsBothExecutions_ThroughDurableJournalAndBroker()
    {
        await using var central = await databases.CreateAsync();
        await using var source = await IdentityJourneyDatabase.CreateAsync();
        await JourneyDatabaseOperation.RunAsync(() => CostingDatabase.MigrateAsync(source.ConnectionString));
        foreach (var assembly in new[] { typeof(CostingHostMarker).Assembly.Location, typeof(GatewayHostMarker).Assembly.Location })
        {
            var migration = BusinessProcess.StartInfo(assembly, "OperationJournal", source.ConnectionString);
            migration.ArgumentList.Add("migrate-operation-journal");
            var result = await JourneyDatabaseOperation.RunAsync(() => BusinessProcess.RunToExitAsync(migration));
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("OperationJournal migrations applied.", result.Output, StringComparison.Ordinal);
        }
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-coverage", ClientName = prefix };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var topology = EventTopology.Create(broker.ExchangeName, [facts, operations]);
        var routePath = Path.Combine(Path.GetTempPath(), $"nsn-operation-coverage-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var centralSettings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
            centralSettings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "operation-coverage-root", settings: centralSettings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "operation-coverage-root");
            var sourceSettings = new Dictionary<string, string>(centralSettings, StringComparer.Ordinal)
            {
                ["OperationJournal__Storage__Provider"] = "Postgres",
                ["ConnectionStrings__OperationJournal"] = source.ConnectionString,
                ["Costing__Messaging__Enabled"] = "false",
            };
            var traceId = Guid.NewGuid().ToString("N");
            var offlineSettings = new Dictionary<string, string>(sourceSettings, StringComparer.Ordinal) { ["RabbitMQ__HostName"] = string.Empty };
            await using (var offlineCosting = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
                "Costing", source.ConnectionString, settings: offlineSettings))
            {
                await WriteRoutesAsync(offlineCosting.Client.BaseAddress!);
                await using var offlineGateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath, offlineSettings);
                offlineGateway.Authenticate();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/costing/cost")
                {
                    Content = JsonContent.Create(new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, purchaseCost = 72m, freightCost = 8m }),
                };
                Assert.True(request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-1234567890abcdef-01"));
                request.Headers.Add("X-Correlation-Id", "costing-safe-62");
                using var accepted = await offlineGateway.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                Assert.Equal("costing-safe-62", Assert.Single(accepted.Headers.GetValues("X-Correlation-Id")));
                await using var storage = new ServiceCollection().AddOperationJournalPostgresStorage(source.ConnectionString).BuildServiceProvider();
                await using var scope = storage.CreateAsyncScope();
                var pending = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
                await GatewayResilienceTests.EventuallyAsync(async () => (await OperationEndpointInventoryTests.ReadAsync(pending))
                    .Count(item => item.TraceId == traceId && item.Phase == "finished") == 2);
                using var absent = await reader.Client.GetAsync(new Uri($"/api/auditing/operations?traceId={traceId}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
                Assert.Empty((await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
            }
            // 两个来源进程全部退出后再接入 broker，验证 journal 确实跨进程保留。
            await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
                "Costing", source.ConnectionString, settings: sourceSettings);
            await WriteRoutesAsync(costing.Client.BaseAddress!);
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath, sourceSettings);

            var observed = await WaitForBothExecutionsAsync(reader.Client, traceId);
            var gatewayRecord = Assert.Single(observed, item => item.GetProperty("source").GetString() == "gateway");
            var costingRecord = Assert.Single(observed, item => item.GetProperty("source").GetString() == "costing");
            Assert.Equal("proxy", gatewayRecord.GetProperty("metadata").GetProperty("executionRole").GetString());
            Assert.Equal("gateway.forward", gatewayRecord.GetProperty("metadata").GetProperty("action").GetString());
            Assert.Equal("endpoint", costingRecord.GetProperty("metadata").GetProperty("executionRole").GetString());
            Assert.Equal("1234567890abcdef", gatewayRecord.GetProperty("metadata").GetProperty("parentSpanId").GetString());
            Assert.NotEqual(gatewayRecord.GetProperty("metadata").GetProperty("spanId").GetString(),
                costingRecord.GetProperty("metadata").GetProperty("spanId").GetString());
            Assert.Equal(2, observed.Select(item => item.GetProperty("operationId").GetGuid()).Distinct().Count());
            Assert.Contains(observed, item => item.GetProperty("source").GetString() == "gateway"
                && item.GetProperty("routeTemplate").GetString() == "/api/costing/{**catch-all}");
            Assert.Contains(observed, item => item.GetProperty("source").GetString() == "costing"
                && item.GetProperty("routeTemplate").GetString() == "/api/costing/cost");
            Assert.All(observed, item =>
            {
                Assert.Equal("accepted", item.GetProperty("outcome").GetString());
                Assert.Equal(202, item.GetProperty("statusCode").GetInt32());
                Assert.Equal("test-operator", item.GetProperty("actorId").GetString());
                Assert.Equal("costing-safe-62", item.GetProperty("metadata").GetProperty("correlationId").GetString());
                Assert.NotEqual(JsonValueKind.Null, item.GetProperty("startedAt").ValueKind);
                Assert.NotEqual(JsonValueKind.Null, item.GetProperty("finishedAt").ValueKind);
            });
            gateway.Authenticate(root: false);
            await RejectedAsync("/api/costing/cost", new { }, HttpStatusCode.Forbidden, "test-reader");
            gateway.Authenticate();
            await RejectedAsync("/api/costing/cost", new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), purchaseCost = -1m, freightCost = 0m }, HttpStatusCode.BadRequest);
            await OperationJournalGatewayTests.SetJournalStorageAvailableAsync(source.ConnectionString, available: false);
            try
            {
                using var acceptedDuringOutage = await gateway.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                    new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), purchaseCost = 10m, freightCost = 2m });
                Assert.Equal(HttpStatusCode.Accepted, acceptedDuringOutage.StatusCode);
                var taskId = (await acceptedDuringOutage.Content.ReadApiDataAsync()).GetProperty("taskId").GetGuid();
                for (var probe = 0; probe < 4; probe++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    using var found = await gateway.Client.GetAsync(new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, found.StatusCode);
                }
                await CheckHealthAsync();
            }
            finally { await OperationJournalGatewayTests.SetJournalStorageAvailableAsync(source.ConnectionString, available: true); }
            await RejectedAsync("/api/costing/cost", new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), purchaseCost = -1m, freightCost = 0m }, HttpStatusCode.BadRequest);
            await CheckHealthAsync(); // 后续恢复投递不能抹掉故障期的缺口。

            async Task CheckHealthAsync()
            {
                foreach (var client in new[] { costing.Client, gateway.Client })
                {
                    using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                    Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
                    using var logging = await client.GetAsync(new Uri("/health/logging", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, logging.StatusCode);
                    Assert.Equal("Degraded", await logging.Content.ReadAsStringAsync());
                }
            }

            async Task RejectedAsync(string path, object body, HttpStatusCode expected, string actor = "test-operator")
            {
                var rejectedTrace = Guid.NewGuid().ToString("N");
                using var rejectedRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
                rejectedRequest.Headers.Add("traceparent", $"00-{rejectedTrace}-1234567890abcdef-01");
                rejectedRequest.Headers.Add("X-User-Id", "forged-actor");
                using var response = await gateway.Client.SendAsync(rejectedRequest);
                Assert.Equal(expected, response.StatusCode);
                Assert.All(await WaitForBothExecutionsAsync(reader.Client, rejectedTrace), item =>
                {
                    Assert.Equal("rejected", item.GetProperty("outcome").GetString());
                    Assert.Equal((int)expected, item.GetProperty("statusCode").GetInt32());
                    Assert.Equal(actor, item.GetProperty("actorId").GetString());
                    Assert.DoesNotContain("forged-actor", item.GetRawText(), StringComparison.Ordinal);
                });
            }

            Task WriteRoutesAsync(Uri address) => File.WriteAllTextAsync(routePath, JsonSerializer.Serialize(new
            {
                routes = new[] { new { routeId = "costing", clusterId = "costing", path = "/api/costing/{**catch-all}", requireAuthentication = true } },
                clusters = new[] { new
                {
                    clusterId = "costing", destinations = new[] { new { name = "primary", address = address.ToString() } },
                    healthCheck = new { path = "/health/ready", interval = "00:00:01", timeout = "00:00:01", failureThreshold = 2 },
                } },
            }));
        }
        finally
        {
            File.Delete(routePath);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task<JsonElement[]> WaitForBothExecutionsAsync(HttpClient client, string traceId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(new Uri($"/api/auditing/operations?traceId={traceId}", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var items = page.GetProperty("data").EnumerateArray().Select(item => item.Clone()).ToArray();
                if (items.Length == 2 && items.All(item => item.GetProperty("finishedAt").ValueKind != JsonValueKind.Null)) { return items; }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail("The authenticated investigation endpoint did not receive completed gateway and Costing observations.");
            throw;
        }
    }
}
