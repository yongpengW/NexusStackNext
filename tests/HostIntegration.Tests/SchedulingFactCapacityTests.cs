using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SchedulingFactCapacityTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task OversizedFact_RejectsThePlanWithoutConsumingCapacity()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        await JourneyDatabaseOperation.RunAsync(() => context.Database.MigrateAsync());
        await context.Database.ExecuteSqlRawAsync("UPDATE scheduling.fact_capacity SET \"MaxRecords\" = 1, \"MaxRecordPayloadBytes\" = 1");
        await using var provider = BuildStorage(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var plan = CreatePlan(77003, DateTimeOffset.UtcNow);
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(plan)).Error);
        Assert.Null(await store.FindAsync(plan.Id));
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
        await context.Database.ExecuteSqlRawAsync("UPDATE scheduling.fact_capacity SET \"MaxRecordPayloadBytes\" = 16384");
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.Single(await store.ListAsync());
        Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow));
    }

    [PostgresFact]
    public async Task Restart_PreservesQuotaAndUsage_AndConfirmedCleanupRestoresHttpWrites()
    {
        await using var database = await databases.CreateAsync();
        await SetMaxRecordsAsync(database.ConnectionString, 1);
        var settings = new Dictionary<string, string> { ["Scheduling__Worker__Enabled"] = "false" };
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-capacity-password", settings: settings))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "schedule-capacity-password");
            using var created = await DefineAsync(first.Client, "retained-after-restart");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            await first.CrashAsync();
        }
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-capacity-password", settings: settings);
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "schedule-capacity-password");
        using var rejected = await DefineAsync(restarted.Client, "retry-after-cleanup");
        await AssertCapacityFailureAsync(rejected);
        await using var context = CreateContext(database.ConnectionString);
        var outbox = new EfOutboxStore<SchedulingDbContext>(context);
        var now = DateTimeOffset.UtcNow;
        var fact = Assert.Single(await outbox.ReadPendingAsync(10, now));
        await outbox.MarkDeliveredAsync(fact.Id, now);
        using var stillFull = await DefineAsync(restarted.Client, "retry-after-cleanup");
        await AssertCapacityFailureAsync(stillFull);
        var cleanup = new EfCommittedFactCleanup<SchedulingDbContext>(context, PlanCommittedV1.Name, new(), new FixedClock(now.AddDays(8)));
        Assert.Equal(1, await cleanup.CleanupAsync());
        using var recovered = await DefineAsync(restarted.Client, "retry-after-cleanup");
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        using var cappedAgain = await DefineAsync(restarted.Client, "still-limited-after-restart");
        await AssertCapacityFailureAsync(cappedAgain);
    }

    [PostgresFact]
    public async Task CalendarDecisions_RejectAtomicallyAtCapacity_AndSuccessfulReplayConsumesNothing()
    {
        foreach (var (policy, minutes, kind) in new[] { ("FireOnce", 1, "Triggered"), ("FireOnce", 3, "Coalesced"), ("Skip", 3, "Skipped") })
        {
            await using var database = await IdentityJourneyDatabase.CreateAsync();
            await using var context = CreateContext(database.ConnectionString);
            await JourneyDatabaseOperation.RunAsync(() => context.Database.MigrateAsync());
            await using var provider = BuildStorage(database.ConnectionString);
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
            var beginning = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
            var rule = new ScheduleRule("Cron", "* * * * *", "Etc/UTC", 5, MisfirePolicy: policy, GraceSeconds: 30);
            var plan = ScheduledTask.Create(new ScheduledTaskId(77002), TaskCode.Create("calendar-capacity").Value, rule,
                beginning.AddMinutes(1), ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
            Assert.True((await store.AddAsync(plan)).IsSuccess);
            await SetMaxRecordsAsync(database.ConnectionString, 1);
            var clock = new FixedClock(beginning.AddMinutes(minutes));
            var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());
            var rejected = await runner.RunOnceAsync();
            Assert.Equal(new[] { plan.Id.Value }, rejected.FailedPlanIds);
            Assert.Equal(0, rejected.Triggered);
            Assert.Equal(0, rejected.Skipped);
            Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
            Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
            var unchanged = await store.FindAsync(plan.Id);
            Assert.NotNull(unchanged);
            Assert.Equal(1, unchanged.Version);
            Assert.Equal(plan.NextRunAt, unchanged.NextRunAt);
            Assert.Null(unchanged.RetryAt);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
            Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));

            await SetMaxRecordsAsync(database.ConnectionString, 2);
            var success = await runner.RunOnceAsync();
            Assert.Empty(success.FailedPlanIds);
            Assert.Equal(kind == "Skipped" ? 0 : 1, success.Triggered);
            Assert.Equal(kind == "Skipped" ? 1 : 0, success.Skipped);
            var current = await store.FindAsync(plan.Id);
            Assert.NotNull(current);
            Assert.Equal(2, current.Version);
            var decision = Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
            Assert.Equal(kind, decision.Kind);
            var entries = await outbox.ReadPendingAsync(10, clock.UtcNow);
            ScheduleOccurrence? occurrence = null;
            if (kind != "Skipped")
            {
                var delivered = Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
                occurrence = new ScheduleOccurrence(delivered.OccurrenceId, plan.Id.Value, delivered.TriggerSequence,
                    delivered.ScheduledAt, delivered.TriggeredAt, delivered.TargetKind, delivered.TargetId, "42");
                await outbox.MarkDeliveredAsync(delivered.OccurrenceId, clock.UtcNow);
            }
            else { Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items); }
            Assert.Equal(2, entries.Count(entry => entry.EventName == PlanCommittedV1.Name));
            Assert.Equal(kind == "Skipped" ? 2 : 3, entries.Count);
            Assert.True((await store.RecordDecisionAsync(current, 1, decision, occurrence)).IsSuccess);
            var cleanup = new EfCommittedFactCleanup<SchedulingDbContext>(context, PlanCommittedV1.Name, new(), new FixedClock(clock.UtcNow.AddDays(8)));
            Assert.Equal(0, await cleanup.CleanupAsync());
            if (occurrence is not null)
            {
                Assert.Equal("Delivered", Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items).DeliveryState);
            }
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
        }
    }

    [PostgresFact]
    public async Task LastSlot_HasOneWinner_AndOnlyExpiredConfirmedCleanupEnablesSameScopeRetry()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        await JourneyDatabaseOperation.RunAsync(() => context.Database.MigrateAsync());
        await SetMaxRecordsAsync(database.ConnectionString, 1);
        await using var provider = BuildStorage(database.ConnectionString);
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var attempt = provider.CreateAsyncScope();
            var candidate = CreatePlan(78000 + index, now);
            var result = await attempt.ServiceProvider.GetRequiredService<IScheduledTaskStore>().AddAsync(candidate);
            if (result.IsFailure) { Assert.Equal(TaskRegistry.AuditCapacityExceeded, result.Error); }
            return (candidate.Id, result.IsSuccess);
        }));
        var winner = Assert.Single(attempts, attempt => attempt.IsSuccess);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var plan = Assert.Single(await store.ListAsync());
        Assert.Equal(winner.Id, plan.Id);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        var deferred = plan.Snapshot();
        Assert.True(deferred.Defer(now, "scheduling.commit.failed").IsSuccess);
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.SaveAsync(deferred, 1)).Error);
        var unchanged = await store.FindAsync(plan.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.Version);
        Assert.Null(unchanged.RetryAt);
        Assert.Equal(0, unchanged.SchedulingFailureCount);
        plan.Disable();
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.SaveAsync(plan, 1)).Error);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now));
        await outbox.MarkDeliveredAsync(original.Id, now);
        var cleanup = new EfCommittedFactCleanup<SchedulingDbContext>(context, PlanCommittedV1.Name, new(), new FixedClock(now));
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.SaveAsync(plan, 1)).Error);
        var expired = new EfCommittedFactCleanup<SchedulingDbContext>(context, PlanCommittedV1.Name, new(), new FixedClock(now.AddDays(8)));
        Assert.Equal(1, await expired.CleanupAsync());
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        var changed = await store.FindAsync(plan.Id);
        Assert.NotNull(changed);
        Assert.False(changed.IsEnabled);
        Assert.Equal(2, changed.Version);

        var retained = Assert.Single(await outbox.ReadPendingAsync(10, now));
        Assert.Equal(0, await expired.CleanupAsync()); // 未交付副本仍占唯一额度。
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(CreatePlan(79000, now))).Error);
        Assert.True(await outbox.MarkDeadLetteredAsync(retained.Id, "test.delivery.exhausted", now, retained.RetryRevision));
        Assert.Equal(0, await expired.CleanupAsync()); // 死信不能被清理释放容量。
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(CreatePlan(79000, now))).Error);
        Assert.Single(await store.ListAsync());
    }

    [PostgresFact]
    public async Task RecoveryDecision_WhenOnlyOneOfTwoFactsFits_ReportsFailureAndRollsBackTheEntireDecision()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        await JourneyDatabaseOperation.RunAsync(() => context.Database.MigrateAsync());
        await using var provider = BuildStorage(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var plan = CreatePlan(77001, now);
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Defer(now, "scheduling.commit.failed").IsSuccess);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        await SetMaxRecordsAsync(database.ConnectionString, 3); // 创建和退避占两条；恢复加触发需要两条。
        var clock = new FixedClock(now.AddMinutes(1));
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());

        var rejected = await runner.RunOnceAsync();
        Assert.Equal(1, rejected.Examined);
        Assert.Equal(0, rejected.Triggered);
        Assert.Equal(0, rejected.Skipped);
        Assert.Equal(new[] { plan.Id.Value }, rejected.FailedPlanIds);
        var unchanged = await store.FindAsync(plan.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(2, unchanged.Version);
        Assert.Equal(now, unchanged.NextRunAt);
        Assert.Equal(plan.RetryAt, unchanged.RetryAt);
        Assert.Equal(1, unchanged.SchedulingFailureCount);
        Assert.Equal(0, unchanged.TriggerSequence);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow)).Count);

        await SetMaxRecordsAsync(database.ConnectionString, 4);
        var recovered = await runner.RunOnceAsync();
        Assert.Equal(1, recovered.Triggered);
        Assert.Empty(recovered.FailedPlanIds);
        var current = await store.FindAsync(plan.Id);
        Assert.NotNull(current);
        Assert.Equal(3, current.Version);
        Assert.Equal(1, current.TriggerSequence);
        Assert.Equal(0, current.SchedulingFailureCount);
        Assert.Null(current.RetryAt);
        var decision = Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        var occurrence = Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal("Triggered", decision.Kind);
        Assert.Equal(decision.OccurrenceId, occurrence.OccurrenceId);
        var pending = await outbox.ReadPendingAsync(10, now.AddDays(1));
        Assert.Equal(4, pending.Count(entry => entry.EventName == PlanCommittedV1.Name));
        Assert.Single(pending, entry => entry.EventName == ScheduleTriggeredV1.Name);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
    }

    [PostgresFact]
    public async Task FullCapacity_RejectsManagementChanges_WhileNoOpsPreserveThePlan()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-capacity-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-capacity-password");
        await SetMaxRecordsAsync(database.ConnectionString, 1);
        using var created = await DefineAsync(client, "first-capacity-plan");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();

        using var rejected = await DefineAsync(client, "second-capacity-plan");
        await AssertCapacityFailureAsync(rejected);
        using var pause = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        await AssertCapacityFailureAsync(pause);
        using var resume = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = 1 });
        await AssertCapacityFailureAsync(resume); // 恢复会重排下次时刻，属于真实修改。
        using var changedRule = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative),
            new { expectedVersion = 1, rule = new { kind = "Interval", intervalSeconds = 7200 } });
        await AssertCapacityFailureAsync(changedRule);
        using var unchangedRule = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative),
            new { expectedVersion = 1, rule = new { kind = "Interval", intervalSeconds = 3600 } });
        Assert.Equal(HttpStatusCode.NoContent, unchangedRule.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var plan = Assert.Single(await store.ListAsync());
        Assert.Equal(new ScheduledTaskId(id), plan.Id);
        Assert.True(plan.IsEnabled);
        Assert.Equal(1, plan.Version);
    }

    private static Task<HttpResponseMessage> DefineAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code,
            intervalSeconds = 3600,
            firstRunInSeconds = 3600,
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });

    private static ScheduledTask CreatePlan(long id, DateTimeOffset now) => ScheduledTask.Create(
        new ScheduledTaskId(id), TaskCode.Create($"capacity-plan-{id}").Value, TimeSpan.FromHours(1), now,
        ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;

    private static SchedulingDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<SchedulingDbContext>().UseNexusStackPostgres(connectionString, SchedulingDbContext.SchemaName).Options);

    private static ServiceProvider BuildStorage(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 22 });
        services.AddSingleton<IScheduleCalendar, CronScheduleCalendar>();
        services.AddSchedulingPostgresStorage(connectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task SetMaxRecordsAsync(string connectionString, long limit)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE scheduling.fact_capacity SET \"MaxRecords\" = @limit", connection);
        command.Parameters.AddWithValue("limit", limit);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertCapacityFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scheduling.audit_capacity_exhausted", error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
    }
}
