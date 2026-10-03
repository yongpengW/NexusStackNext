using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Auditing.Contracts;
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
using NexusStackNext.Scheduling.Contracts;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class ScheduledCostBusinessJourneyTests
{
    [AuditBrokerFact]
    public async Task CostingCrashBeforeReceiptCommit_RollsBackTaskAndInbox_AndRedeliveryRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await TaskOperationJourneyTests.MigrateJournalAsync(typeof(CostingHostMarker).Assembly.Location, database.ConnectionString);
        await using var central = await IdentityJourneyDatabase.CreateAsync();
        await central.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-schedule-crash", ClientName = prefix };
        var subscription = new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = prefix + "-costing" };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-audit" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, facts, operations]);
        var settings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
        settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
        settings["Costing__Messaging__Enabled"] = "true";
        settings["Costing__Scheduling__ConsumerName"] = subscription.ConsumerName;
        var message = new ScheduleTriggeredV1
        {
            EventId = Guid.NewGuid(),
            PlanId = 430043,
            TriggerSequence = 1,
            TargetKind = CostingScheduleTarget.Recalculate,
            TargetId = Guid.NewGuid(),
            CreatedBy = "42",
            ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "schedule-observation-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "schedule-observation-root");
            settings["OperationJournal__Storage__Provider"] = "Postgres";
            settings["ConnectionStrings__OperationJournal"] = database.ConnectionString;
            var observationPath = $"/api/auditing/operations?source=costing&subjectType={ScheduleTriggeredV1.Name}&subjectId={message.EventId:D}";
            Guid interruptedOperation;
            await using (var pause = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString))
            await using (var first = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings))
            {
                first.Authenticate();
                using var submitted = await first.Client.PostAsJsonAsync(Relative("/api/costing/cost"),
                    new { requestId = Guid.NewGuid(), itemId = message.TargetId, expectedVersion = 0, purchaseCost = 80m, freightCost = 20m });
                Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
                await pause.OpenAsync();
                await using (var barrier = new NpgsqlCommand("""
                    SELECT pg_advisory_lock(430043);
                    CREATE FUNCTION costing.pause_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM pg_advisory_xact_lock(430043); RETURN NEW; END $$;
                    CREATE TRIGGER pause_receipt BEFORE INSERT ON costing.schedule_receipts
                    FOR EACH ROW EXECUTE FUNCTION costing.pause_receipt();
                    """, pause))
                {
                    await barrier.ExecuteNonQueryAsync();
                }
                await using var bus = new RabbitMqEventBus(broker);
                Assert.True((await bus.PublishAsync(OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope())).IsSuccess);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    await using var blocked = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'", pause);
                    if ((long)(await blocked.ExecuteScalarAsync(timeout.Token))! > 0) { break; }
                    await Task.Delay(100, timeout.Token);
                }
                using var noReceipt = await first.Client.GetAsync(Relative($"/api/costing/schedule-receipts/{message.EventId}"));
                Assert.Equal(HttpStatusCode.NotFound, noReceipt.StatusCode);
                using var noTask = await first.Client.GetAsync(Relative($"/api/costing/tasks/{message.EventId}"));
                Assert.Equal(HttpStatusCode.NotFound, noTask.StatusCode);
                var observed = await WaitAsync(reader.Client, observationPath, data => data.GetArrayLength() == 1);
                var started = Assert.Single(observed.EnumerateArray());
                Assert.Equal("unconfirmed", started.GetProperty("outcome").GetString());
                interruptedOperation = started.GetProperty("operationId").GetGuid();
                // BusinessProcess 的释放直接终止进程；先终止消费者，再释放外部故障屏障。
            }
            await using var recovered = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
            recovered.Authenticate();
            var receipt = await WaitAsync(recovered.Client, $"/api/costing/schedule-receipts/{message.EventId}", data => data.GetProperty("decision").GetString() == "Accepted");
            Assert.Equal(message.EventId, receipt.GetProperty("taskId").GetGuid());
            var task = await WaitAsync(recovered.Client, $"/api/costing/tasks/{message.EventId}", _ => true);
            Assert.Equal("Pending", task.GetProperty("state").GetString());
            var observations = await WaitAsync(reader.Client, observationPath, data => data.GetArrayLength() == 2
                && data.EnumerateArray().Any(item => item.GetProperty("outcome").GetString() == "accepted"));
            var abandoned = Assert.Single(observations.EnumerateArray(), item => item.GetProperty("operationId").GetGuid() == interruptedOperation);
            Assert.Equal("unconfirmed", abandoned.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, abandoned.GetProperty("finishedAt").ValueKind);
            var accepted = Assert.Single(observations.EnumerateArray(), item => item.GetProperty("outcome").GetString() == "accepted");
            Assert.Equal(JsonValueKind.Null, accepted.GetProperty("actorId").ValueKind);
            Assert.Equal("costing.schedule.accept", accepted.GetProperty("metadata").GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, accepted.GetProperty("metadata").GetProperty("initiatorId").ValueKind);
            Assert.Equal(accepted.GetProperty("operationId").GetGuid(), task.GetProperty("executionOrigin").GetProperty("operationId").GetGuid());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public Task GatewaySchedule_SurvivesCostingOutageAndCreatorLogout_ThenCompletesWithoutInventingBusinessVersions() =>
        VerifyScheduledBusinessJourneyAsync(calendar: false);

    [AuditBrokerFact]
    public Task GatewayCalendar_SurvivesCostingOutageAndCreatorLogout_ThenCompletesThroughPricing() =>
        VerifyScheduledBusinessJourneyAsync(calendar: true);

    private static async Task VerifyScheduledBusinessJourneyAsync(bool calendar)
    {
        await using var platformDatabase = await IdentityJourneyDatabase.CreateAsync();
        await platformDatabase.MigrateAsync();
        await using var costingDatabase = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(costingDatabase.ConnectionString);
        await using var pricingDatabase = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(pricingDatabase.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-schedule-business", ClientName = prefix };
        var audit = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-audit" };
        var scheduled = new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = prefix + "-costing" };
        var priced = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [audit, scheduled, priced]);
        var routes = Path.Combine(Path.GetTempPath(), $"nsn-scheduled-cost-routes-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, audit.ConsumerName);
            settings["Jwt__SigningKey"] = BusinessProcess.SigningKey;
            settings["Costing__Messaging__Enabled"] = "true";
            settings["Costing__Scheduling__ConsumerName"] = scheduled.ConsumerName;
            settings["Pricing__Messaging__Enabled"] = "true";
            settings["Pricing__Messaging__ConsumerName"] = priced.ConsumerName;
            await using var platform = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "schedule-root-password", settings: settings);
            await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, worker: true, settings: settings);
            var itemId = Guid.NewGuid();
            JsonElement costBefore;
            JsonElement priceBefore;
            Uri offlineCosting;
            await using (var initial = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costingDatabase.ConnectionString, worker: true, settings: settings))
            {
                offlineCosting = initial.Client.BaseAddress!;
                await WriteRoutesAsync(routes, platform.Client.BaseAddress!, offlineCosting, pricing.Client.BaseAddress!);
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes);
                await HttpInt64OpenApiTests.AssertHostDocumentsAsync(platform.Client, initial.Client, pricing.Client, gateway.Client);
                await PlatformSettingsAccessTests.LoginAsync(gateway.Client, "journey-root", "schedule-root-password");
                using var submitted = await gateway.Client.PostAsJsonAsync(Relative("/api/costing/cost"),
                    new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, purchaseCost = 80m, freightCost = 20m });
                Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
                priceBefore = await WaitAsync(gateway.Client, $"/api/pricing/items/{itemId}", data =>
                    data.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number && data.GetProperty("breakEvenPrice").GetDecimal() == 100m);
                costBefore = await WaitAsync(gateway.Client, $"/api/costing/items/{itemId}", _ => true);
            }

            Guid occurrenceId;
            await using (var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes))
            {
                await PlatformSettingsAccessTests.LoginAsync(gateway.Client, "journey-root", "schedule-root-password");
                if (calendar)
                {
                    using var status = await gateway.Client.GetAsync(Relative("/api/scheduling"));
                    Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                    Assert.Equal(1, (await status.Content.ReadApiDataAsync()).GetProperty("tickIntervalSeconds").GetDouble());
                }
                var secondOffset = DateTimeOffset.UtcNow.AddSeconds(5).Second;
                object definition = calendar
                    ? new { code = "scheduled-cost-journey", rule = new { kind = "Cron", expression = FormattableString.Invariant($"{secondOffset} * * * * *"), timeZoneId = "Asia/Shanghai" }, targetKind = CostingScheduleTarget.Recalculate, targetId = itemId }
                    : new { code = "scheduled-cost-journey", intervalSeconds = 3600, targetKind = CostingScheduleTarget.Recalculate, targetId = itemId };
                using var scheduledPlan = await gateway.Client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
                    definition);
                Assert.Equal(HttpStatusCode.Created, scheduledPlan.StatusCode);
                var planId = (await scheduledPlan.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
                var history = await WaitAsync(gateway.Client, $"/api/scheduling/tasks/{planId}/occurrences", data =>
                    data.GetArrayLength() == 1 && data[0].GetProperty("deliveryState").GetString() == "Delivered");
                occurrenceId = history[0].GetProperty("occurrenceId").GetGuid();
                if (calendar)
                {
                    Assert.Equal(secondOffset, history[0].GetProperty("scheduledAt").GetDateTimeOffset().Second);
                    using var decided = await gateway.Client.GetAsync(Relative($"/api/scheduling/tasks/{planId}/decisions"));
                    var decision = Assert.Single((await decided.Content.ReadApiDataAsync()).EnumerateArray());
                    Assert.Equal("Triggered", decision.GetProperty("kind").GetString());
                    Assert.Equal(6, decision.GetProperty("rule").GetProperty("cronFieldCount").GetInt32());
                    using var pause = await gateway.Client.PostAsJsonAsync(Relative($"/api/scheduling/tasks/{planId}/pause"), new { expectedVersion = "2" });
                    Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
                }
                using var setting = await gateway.Client.PutAsJsonAsync(Relative("/api/platform/settings/schedule.business"), new { value = "committed" });
                Assert.Equal(HttpStatusCode.NoContent, setting.StatusCode);
                await AuditBusinessJourneyTests.WaitForCountAsync(gateway.Client, 1);
                using var unavailable = await gateway.Client.GetAsync(Relative($"/api/costing/schedule-receipts/{occurrenceId}"));
                Assert.Contains(unavailable.StatusCode, new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable });
                using var logout = await gateway.Client.PostAsync(Relative("/api/identity/logout"), null);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
                using var revoked = await gateway.Client.GetAsync(Relative("/api/scheduling/tasks/"));
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            }

            await using var restarted = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costingDatabase.ConnectionString, worker: true, settings: settings);
            await WriteRoutesAsync(routes, platform.Client.BaseAddress!, restarted.Client.BaseAddress!, pricing.Client.BaseAddress!);
            await using var recoveredGateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes);
            await PlatformSettingsAccessTests.LoginAsync(recoveredGateway.Client, "journey-root", "schedule-root-password");
            var receipt = await WaitAsync(recoveredGateway.Client, $"/api/costing/schedule-receipts/{occurrenceId}", data => data.GetProperty("decision").GetString() == "Accepted");
            Assert.Equal(occurrenceId, receipt.GetProperty("taskId").GetGuid());
            var scheduledTask = await WaitAsync(recoveredGateway.Client, $"/api/costing/tasks/{occurrenceId}", data => data.GetProperty("state").GetString() == "Succeeded");
            await WaitAsync(recoveredGateway.Client, $"/api/costing/tasks/{occurrenceId}/delivery", data => data.GetProperty("state").GetString() == "Delivered");
            var costAfter = await WaitAsync(recoveredGateway.Client, $"/api/costing/items/{itemId}", _ => true);
            Assert.Equal(costBefore.GetProperty("version").ReadHttpInt64(), costAfter.GetProperty("version").ReadHttpInt64());
            Assert.Equal(100m, costAfter.GetProperty("unitCost").GetDecimal());
            var priceAfter = await WaitAsync(recoveredGateway.Client, $"/api/pricing/items/{itemId}", _ => true);
            Assert.Equal(priceBefore.GetProperty("version").ReadHttpInt64(), priceAfter.GetProperty("version").ReadHttpInt64());
            Assert.Equal(100m, priceAfter.GetProperty("breakEvenPrice").GetDecimal());
            // 后续真实成本变更是消费顺序屏障：前一条同输入结果不能偷偷多推进版本。
            using var changed = await recoveredGateway.Client.PostAsJsonAsync(Relative("/api/costing/cost"), new
            {
                requestId = Guid.NewGuid(),
                itemId,
                expectedVersion = costAfter.GetProperty("version").ReadHttpInt64(),
                purchaseCost = 90m,
                freightCost = 30m,
            });
            Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);
            var advanced = await WaitAsync(recoveredGateway.Client, $"/api/pricing/items/{itemId}", data =>
                data.GetProperty("costingRevision").ReadHttpInt64() == 2 && data.GetProperty("breakEvenPrice").GetDecimal() == 120m);
            Assert.Equal(priceBefore.GetProperty("version").ReadHttpInt64() + 2, advanced.GetProperty("version").ReadHttpInt64());
            var origin = scheduledTask.GetProperty("executionOrigin");
            var rootId = origin.GetProperty("rootOperationId").GetGuid();
            var chain = await WaitAsync(recoveredGateway.Client, $"/api/auditing/operations?rootSource=platform&rootOperationId={rootId:D}", data =>
                data.EnumerateArray().Any(item => item.GetProperty("source").GetString() == "pricing" && item.GetProperty("outcome").GetString() == "skipped"));
            var operations = chain.EnumerateArray().ToArray();
            var definitionOperation = Assert.Single(operations, item => item.GetProperty("operationId").GetGuid() == rootId);
            Assert.Equal("http", definitionOperation.GetProperty("kind").GetString());
            var creator = definitionOperation.GetProperty("actorId").GetString();
            Assert.Equal(receipt.GetProperty("createdBy").GetString(), creator);
            var decisionOperation = Assert.Single(operations, item => item.GetProperty("kind").GetString() == "schedule");
            var received = Assert.Single(operations, item => item.GetProperty("source").GetString() == "costing" && item.GetProperty("kind").GetString() == "message");
            Assert.Equal("accepted", received.GetProperty("outcome").GetString());
            Assert.Equal(occurrenceId.ToString("D"), received.GetProperty("metadata").GetProperty("subjectId").GetString());
            Assert.Equal(ScheduleTriggeredV1.Name, received.GetProperty("metadata").GetProperty("subjectType").GetString());
            Assert.Equal(decisionOperation.GetProperty("operationId").GetGuid(), received.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            Assert.Equal(received.GetProperty("operationId").GetGuid(), origin.GetProperty("operationId").GetGuid());
            var calculated = Assert.Single(operations, item => item.GetProperty("kind").GetString() == "task");
            Assert.Equal("costing", calculated.GetProperty("source").GetString());
            Assert.Equal("completed", calculated.GetProperty("outcome").GetString());
            Assert.Equal(received.GetProperty("operationId").GetGuid(), calculated.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            var ignoredResult = Assert.Single(operations, item => item.GetProperty("source").GetString() == "pricing");
            Assert.Equal("message", ignoredResult.GetProperty("kind").GetString());
            Assert.Equal(calculated.GetProperty("operationId").GetGuid(), ignoredResult.GetProperty("metadata").GetProperty("parentOperationId").GetGuid());
            Assert.All(operations.Where(item => item.GetProperty("kind").GetString() != "http"), item =>
            {
                Assert.Equal(JsonValueKind.Null, item.GetProperty("actorId").ValueKind);
                Assert.Equal(creator, item.GetProperty("metadata").GetProperty("initiatorId").GetString());
            });
        }
        finally
        {
            File.Delete(routes);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
        }
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, string path, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var status = 0;
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(Relative(path), timeout.Token);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                    var data = page.GetProperty("data");
                    if (predicate(data)) { return data.Clone(); }
                }
                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException($"等待 {path} 未达到预期，最后 HTTP 状态 {status}。"); }
    }

    private static async Task WriteRoutesAsync(string path, Uri platform, Uri costing, Uri pricing)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NexusStackNext.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src/Gateway/NexusStackNext.Gateway/routes.business.json")))!;
        var clusters = table["clusters"]!.AsArray();
        Assert.Equal(3, clusters.Count);
        foreach (var cluster in clusters)
        {
            var address = cluster!["clusterId"]!.GetValue<string>() switch
            {
                "platform-host" => platform,
                "costing-host" => costing,
                "pricing-host" => pricing,
                _ => throw new InvalidOperationException("未识别的业务路由上下文。"),
            };
            foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address.ToString(); }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
