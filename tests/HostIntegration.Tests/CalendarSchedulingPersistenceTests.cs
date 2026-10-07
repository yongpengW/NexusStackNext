using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class CalendarSchedulingPersistenceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task IntervalRule_NormalizesSubMicrosecondInput_WithoutFalseRuleChanges()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "calendar-root-password");
        foreach (var example in new[]
        {
            (Code: "sub-microsecond", Seconds: 30.1234567, Expected: 30.123457, At: "2026-10-02T00:00:30.123457+00:00"),
            (Code: "double-roundtrip", Seconds: 1.000004, Expected: 1.000004, At: "2026-10-02T00:00:01.000004+00:00"),
            (Code: "even-lower", Seconds: 1.0000005, Expected: 1.0, At: "2026-10-02T00:00:01+00:00"),
            (Code: "even-upper", Seconds: 1.0000015, Expected: 1.000002, At: "2026-10-02T00:00:01.000002+00:00"),
        })
        {
            var rule = new { kind = "Interval", intervalSeconds = example.Seconds };
            using var preview = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative),
                new { rule, after = "2026-10-02T00:00:00Z" });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal(example.At, Assert.Single((await preview.Content.ReadApiDataAsync()).GetProperty("times").EnumerateArray()).GetProperty("utc").GetString());
            // 旧请求形状也必须使用相同规范化；更新时使用新的显式规则形状。
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = example.Code,
                intervalSeconds = example.Seconds,
                targetKind = "costing.recalculate",
                targetId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            using var same = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative), new { expectedVersion = "1", rule });
            Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
            var plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            var plan = Assert.Single(plans.GetProperty("data").EnumerateArray(), item => item.GetProperty("taskId").ReadHttpInt64() == id);
            Assert.Equal(1, plan.GetProperty("version").ReadHttpInt64());
            Assert.Equal(1, plan.GetProperty("scheduleRevision").ReadHttpInt64());
            Assert.Equal(example.Expected, plan.GetProperty("intervalSeconds").GetDouble());
            using var roundtrip = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative),
                new { expectedVersion = "1", rule = plan.GetProperty("rule") });
            Assert.Equal(HttpStatusCode.NoContent, roundtrip.StatusCode);
            var after = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            Assert.Equal(1, Assert.Single(after.GetProperty("data").EnumerateArray(), item => item.GetProperty("taskId").ReadHttpInt64() == id).GetProperty("version").ReadHttpInt64());
        }
    }

    [PostgresFact]
    public async Task ProcessCrashDuringDecisionCommit_LeavesNoPartialWork_AndRestartRegistersOneOccurrence()
    {
        await using var database = await databases.CreateAsync();
        await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        await barrier.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            SELECT pg_advisory_lock(450045);
            CREATE FUNCTION scheduling.pause_decision() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(450045); RETURN NEW; END $$;
            CREATE TRIGGER pause_decision BEFORE INSERT ON scheduling.decisions
            FOR EACH ROW EXECUTE FUNCTION scheduling.pause_decision();
            """, barrier))
        {
            await install.ExecuteNonQueryAsync();
        }
        long id;
        DateTimeOffset scheduledAt;
        int blockedBackend;
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "calendar-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "calendar-root-password");
            var second = DateTimeOffset.UtcNow.AddSeconds(5).Second;
            using var created = await first.Client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = "crash-calendar",
                rule = new { kind = "Cron", expression = FormattableString.Invariant($"{second} * * * * *"), timeZoneId = "UTC" },
                targetKind = "costing.recalculate",
                targetId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                await using var blocked = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)) LIMIT 1", barrier);
                blocked.Parameters.AddWithValue("barrier", barrier.ProcessID);
                if (await blocked.ExecuteScalarAsync(timeout.Token) is int backend) { blockedBackend = backend; break; }
                await Task.Delay(50, timeout.Token);
            }
            var page = await first.Client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
            scheduledAt = plan.GetProperty("nextRunAt").GetDateTimeOffset();
            Assert.Equal(1, plan.GetProperty("version").ReadHttpInt64());
            foreach (var history in new[] { "decisions", "occurrences" })
            {
                using var response = await first.Client.GetAsync(new Uri($"/api/scheduling/tasks/{id}/{history}", UriKind.Relative));
                Assert.Empty((await response.Content.ReadApiDataAsync()).EnumerateArray());
            }
            await first.CrashAsync();
        }
        // 应用退出不保证正在等待锁的 PG backend 已退出。先放行并等其回滚退出，再做需要表锁的 DDL。
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(450045)", barrier))
        {
            Assert.True((bool)(await release.ExecuteScalarAsync())!);
        }
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (true)
            {
                await using var remaining = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pid = @backend)", barrier);
                remaining.Parameters.AddWithValue("backend", blockedBackend);
                if (!(bool)(await remaining.ExecuteScalarAsync(timeout.Token))!) { break; }
                await Task.Delay(50, timeout.Token);
            }
        }
        await using (var recover = new NpgsqlCommand("""
            DROP TRIGGER pause_decision ON scheduling.decisions;
            DROP FUNCTION scheduling.pause_decision();
            """, barrier))
        {
            await recover.ExecuteNonQueryAsync();
        }
        // 恢复扫描前先证明旧事务没有提交，避免把旧事务迟到的提交误认作重启后的成功恢复。
        await using (var observer = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password", schedulingWorkerEnabled: false))
        {
            using var client = observer.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "calendar-root-password");
            var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
            Assert.Equal(1, plan.GetProperty("version").ReadHttpInt64());
            Assert.Equal(scheduledAt, plan.GetProperty("nextRunAt").GetDateTimeOffset());
            foreach (var history in new[] { "decisions", "occurrences" })
            {
                using var response = await client.GetAsync(new Uri($"/api/scheduling/tasks/{id}/{history}", UriKind.Relative));
                Assert.Empty((await response.Content.ReadApiDataAsync()).EnumerateArray());
            }
            var onlyFact = Assert.Single(await ReadFactsAsync(observer.Services));
            Assert.Equal(id, onlyFact.PlanId);
            Assert.Equal("created", onlyFact.Operation);
            Assert.Equal(1, onlyFact.Version);
        }
        await using var secondHost = await PlatformHostProcess.StartAsync(database.ConnectionString, "calendar-root-password");
        await PlatformSettingsAccessTests.LoginAsync(secondHost.Client, "journey-root", "calendar-root-password");
        var occurrence = await SchedulingDeliveryJourneyTests.WaitForDeliveryAsync(secondHost.Client, id, "Pending");
        Assert.Equal(1, occurrence.GetProperty("triggerSequence").ReadHttpInt64());
        Assert.Equal(scheduledAt, occurrence.GetProperty("scheduledAt").GetDateTimeOffset());
        using var historyResponse = await secondHost.Client.GetAsync(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
        var decision = Assert.Single((await historyResponse.Content.ReadApiDataAsync()).EnumerateArray());
        Assert.Equal(2, decision.GetProperty("planVersion").ReadHttpInt64());
        Assert.Equal(occurrence.GetProperty("occurrenceId").GetGuid(), decision.GetProperty("occurrenceId").GetGuid());
    }

    [PostgresFact]
    public async Task ScannerCommit_WinsAgainstStaleManagementSnapshots_IncludingSameRuleUpdates()
    {
        await using var database = await databases.CreateAsync();
        var start = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(start);
        await using var app = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password",
            schedulingWorkerEnabled: false, schedulingClock: clock);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "calendar-root-password");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var action in new[] { "same-rule", "new-rule", "pause", "resume" })
        {
            clock.UtcNow = start;
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = "race-" + action,
                rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" },
                targetKind = "costing.recalculate",
                targetId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            clock.UtcNow = start.AddMinutes(1);
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var block = new NpgsqlCommand("SELECT \"Id\" FROM scheduling.plans WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
            {
                block.Parameters.AddWithValue("id", id);
                await block.ExecuteScalarAsync();
            }
            await using var scope = app.Services.CreateAsyncScope();
            var scan = scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync();
            Task<HttpResponseMessage>? management = null;
            try
            {
                // 让扫描先持有等待位置，再让管理操作读到相同旧版本；两方都必须真的到达写入边界。
                await WaitForBlockedWritersAsync(database.ConnectionString, 1);
                management = ChangeAsync(1);
                await WaitForBlockedWritersAsync(database.ConnectionString, 2);
            }
            finally { await transaction.RollbackAsync(); }
            Assert.Equal(1, (await scan).Triggered);
            Assert.NotNull(management);
            using var rejected = await management;
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            var plan = Assert.Single(page.GetProperty("data").EnumerateArray(), item => item.GetProperty("taskId").ReadHttpInt64() == id);
            Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
            Assert.Equal("2026-10-02T00:02:00+00:00", plan.GetProperty("nextRunAt").GetString());
            var history = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
            var original = Assert.Single(history.GetProperty("data").EnumerateArray()).Clone();
            Assert.Equal(1, original.GetProperty("scheduleRevision").ReadHttpInt64());
            using var accepted = await ChangeAsync(2);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            var retained = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
            Assert.Equal(original.ToString(), Assert.Single(retained.GetProperty("data").EnumerateArray()).ToString());

            Task<HttpResponseMessage> ChangeAsync(long version) => action.EndsWith("rule", StringComparison.Ordinal)
                ? client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative), new
                {
                    expectedVersion = version,
                    rule = new { kind = "Cron", expression = action == "same-rule" ? " * *  * * * " : "*/5 * * * *", timeZoneId = "UTC" },
                })
                : client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/{action}", UriKind.Relative), new { expectedVersion = version });
        }
    }

    private static async Task WaitForBlockedWritersAsync(string connectionString, int expected)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var waiters = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
            if ((long)(await waiters.ExecuteScalarAsync(timeout.Token))! >= expected) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    [PostgresFact]
    public async Task MissingDecisionStorage_RefusesStartup_AndMarksOnlyReadinessUnhealthy()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var damage = new NpgsqlCommand("ALTER TABLE scheduling.decisions RENAME TO unavailable_decisions", connection))
        {
            await damage.ExecuteNonQueryAsync();
        }
        try
        {
            using var unhealthy = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            await using var unprepared = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
            var error = Assert.ThrowsAny<Exception>(() => unprepared.CreateClient());
            Assert.Contains("Scheduling 数据库不可用", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await using var recover = new NpgsqlCommand("ALTER TABLE scheduling.unavailable_decisions RENAME TO decisions", connection);
            await recover.ExecuteNonQueryAsync();
        }
        using var recovered = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [PostgresFact]
    public async Task CalendarDecisions_RollBackTogether_ThenCompetingScannersCommitEachWindowOnce()
    {
        await using var database = await databases.CreateAsync();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var app = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password",
            schedulingWorkerEnabled: false, schedulingClock: clock);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "calendar-root-password");
        var ids = new List<long>();
        foreach (var policy in new[] { "FireOnce", "Skip" })
        {
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = "calendar-" + policy.ToLowerInvariant(),
                rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", misfirePolicy = policy },
                targetKind = "costing.recalculate",
                targetId = Guid.NewGuid(),
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ids.Add((await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64());
        }
        clock.UtcNow = clock.UtcNow.AddMinutes(3);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var fail = new NpgsqlCommand("ALTER TABLE scheduling.decisions ADD CONSTRAINT test_reject_decision CHECK (false)", connection))
        {
            await fail.ExecuteNonQueryAsync();
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync();
            Assert.Equal(ids, result.FailedPlanIds);
            Assert.Equal(0, result.Triggered);
        }
        foreach (var id in ids)
        {
            var decisions = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
            Assert.Empty(decisions.GetProperty("data").EnumerateArray());
            var history = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/occurrences", UriKind.Relative));
            Assert.Empty(history.GetProperty("data").EnumerateArray());
        }
        var rolledBack = await ReadFactsAsync(app.Services);
        Assert.Equal(4, rolledBack.Count);
        foreach (var id in ids)
        {
            Assert.Equal(new[] { "created", "deferred" }, rolledBack.Where(fact => fact.PlanId == id).OrderBy(fact => fact.Version).Select(fact => fact.Operation));
        }
        var before = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.All(before.GetProperty("data").EnumerateArray(), plan =>
        {
            Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
            Assert.Equal("2026-10-02T00:01:00+00:00", plan.GetProperty("nextRunAt").GetString());
            Assert.Equal("2026-10-02T00:04:00+00:00", plan.GetProperty("retryAt").GetString());
            Assert.Equal("scheduling.commit.failed", plan.GetProperty("lastSchedulingErrorCode").GetString());
            Assert.Equal(1, plan.GetProperty("schedulingFailureCount").GetInt32());
        });
        await using (var recover = new NpgsqlCommand("ALTER TABLE scheduling.decisions DROP CONSTRAINT test_reject_decision", connection))
        {
            await recover.ExecuteNonQueryAsync();
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Examined);
        }
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var block = new NpgsqlCommand("SELECT \"Id\" FROM scheduling.plans FOR UPDATE", connection, transaction))
        {
            await block.ExecuteScalarAsync();
        }
        var scans = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync();
        }).ToArray();
        try
        {
            await WaitForBlockedWritersAsync(database.ConnectionString, 2);
        }
        finally { await transaction.RollbackAsync(); }
        var results = await Task.WhenAll(scans);
        Assert.Equal(1, results.Sum(result => result.Triggered));
        Assert.All(results, result => Assert.Empty(result.FailedPlanIds));
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "calendar-root-password",
            settings: new Dictionary<string, string>(StringComparer.Ordinal) { ["Scheduling__Worker__Enabled"] = "false" });
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "calendar-root-password");
        var committed = await ReadFactsAsync(app.Services);
        Assert.Equal(8, committed.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var decisions = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
            var decision = Assert.Single(decisions.GetProperty("data").EnumerateArray());
            Assert.Equal(index == 0 ? "Coalesced" : "Skipped", decision.GetProperty("kind").GetString());
            Assert.Equal(3, decision.GetProperty("planVersion").ReadHttpInt64());
            Assert.Equal(1, decision.GetProperty("scheduleRevision").ReadHttpInt64());
            Assert.Equal("Etc/UTC", decision.GetProperty("rule").GetProperty("timeZoneId").GetString());
            var history = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/occurrences", UriKind.Relative));
            Assert.Equal(index == 0 ? 1 : 0, history.GetProperty("data").GetArrayLength());
            var decided = Assert.Single(committed, fact => fact.PlanId == id && fact.Operation == (index == 0 ? "coalesced" : "skipped"));
            Assert.Equal(3, decided.Version);
            Assert.Equal(decision.GetProperty("decisionId").GetGuid(), decided.DecisionId);
            Assert.Null(decided.ActorId);
            Assert.Equal(decided.Execution!.OperationId, Assert.Single(committed, fact => fact.PlanId == id && fact.Operation == "failure-cleared").Execution!.OperationId);
        }
        var recovered = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.All(recovered.GetProperty("data").EnumerateArray(), plan =>
        {
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("retryAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("lastSchedulingErrorCode").ValueKind);
            Assert.Equal(0, plan.GetProperty("schedulingFailureCount").GetInt32());
            Assert.Equal("2026-10-02T00:05:00+00:00", plan.GetProperty("nextRunAt").GetString());
        });
    }

    private static async Task<IReadOnlyList<PlanCommittedV1>> ReadFactsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        return pending.Where(entry => entry.EventName == PlanCommittedV1.Name).Select(entry => serializer.Deserialize<PlanCommittedV1>(entry.Payload)).ToArray();
    }

    [PostgresFact]
    public async Task RepeatedInitialMigration_PreservesIntervalPlanVersionAndAudit()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        // 开发期无历史数据，旧 DurableOccurrences 升级路径随迁移重置退役。
        // 保留“迁移不能改写既有计划”的义务，通过当前 HTTP 契约准备和观察数据。
        JsonElement before;
        await using (var first = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password", schedulingWorkerEnabled: false))
        {
            using var firstClient = first.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(firstClient, "journey-root", "calendar-root-password");
            using var created = await firstClient.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new { code = "initial-interval", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            using var paused = await firstClient.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
            var firstPage = await firstClient.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            before = Assert.Single(firstPage.GetProperty("data").EnumerateArray()).Clone();
            Assert.Equal(2, before.GetProperty("version").ReadHttpInt64());
            Assert.Equal("Interval", before.GetProperty("rule").GetProperty("kind").GetString());
            Assert.Equal(30, before.GetProperty("intervalSeconds").GetDouble());
            Assert.NotEqual(default, before.GetProperty("audit").GetProperty("createdAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.String, before.GetProperty("audit").GetProperty("updatedAt").ValueKind);
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Scheduling")).ExitCode);
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Scheduling")).ExitCode);
        await using var app = new PersistentIdentityApp(database.ConnectionString, "calendar-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "calendar-root-password");
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(before.ToString(), plan.ToString());
    }

    [PostgresFact]
    public async Task MigrationAndProcessRestart_PreserveCalendarRules_AndLegacyIntervalDefinition()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["Scheduling__Worker__Enabled"] = "false" };
        JsonElement[] before;
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "calendar-root-password", settings: settings))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "calendar-root-password");
            var definitions = new object[]
            {
                new { code = "calendar-cron", rule = new { kind = "Cron", expression = "10 * 7-23 * * ?", timeZoneId = "Asia/Shanghai", misfirePolicy = "Skip", graceSeconds = 10 }, targetKind = "costing.recalculate", targetId = Guid.NewGuid() },
                new { code = "calendar-monthly", rule = new { kind = "MonthlyDay", day = 31, hour = 2, minute = 15, timeZoneId = "Australia/Lord_Howe" }, targetKind = "costing.recalculate", targetId = Guid.NewGuid() },
                new { code = "legacy-interval", intervalSeconds = 30, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() },
            };
            foreach (var definition in definitions)
            {
                using var created = await first.Client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), definition);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }
            var page = await first.Client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            before = page.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(3, before.Length);
            Assert.Equal(new[] { "Cron", "MonthlyDay", "Interval" }, before.Select(item => item.GetProperty("rule").GetProperty("kind").GetString()));
            Assert.Equal(6, before[0].GetProperty("rule").GetProperty("cronFieldCount").GetInt32());
            Assert.Equal("Skip", before[0].GetProperty("rule").GetProperty("misfirePolicy").GetString());
            Assert.Equal(31, before[1].GetProperty("rule").GetProperty("day").GetInt32());
            Assert.Equal(30, before[2].GetProperty("intervalSeconds").GetDouble());
            Assert.All(before, item => Assert.Equal(1, item.GetProperty("scheduleRevision").ReadHttpInt64()));
            await first.CrashAsync();
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Scheduling")).ExitCode);
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "calendar-root-password", settings: settings);
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "calendar-root-password");
        var after = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(before.Select(item => item.ToString()), after.GetProperty("data").EnumerateArray().Select(item => item.ToString()));
    }
}
