using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingOccurrenceTests
{
    [PostgresFact]
    public async Task CompetingScanners_RecordOneOccurrence_AndAdvanceThePlanOnce()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
        var id = await CreatePlanAsync(client, "contended");
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM scheduling.plans FOR UPDATE", blocker, transaction))
        {
            await command.ExecuteScalarAsync();
        }
        var scans = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync();
        }).ToArray();
        try
        {
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var waiters = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
                if ((long)(await waiters.ExecuteScalarAsync(timeout.Token))! >= 2) { break; }
                await Task.Delay(20, timeout.Token);
            }
        }
        finally { await transaction.RollbackAsync(); }
        var results = await Task.WhenAll(scans);
        Assert.Equal(1, results.Sum(result => result.Triggered));
        Assert.Equal(1, results.Sum(result => result.Skipped));
        Assert.All(results, result => Assert.Empty(result.FailedPlanIds));
        var occurrence = Assert.Single(await HistoryAsync(client, id));
        Assert.Equal(1, occurrence.GetProperty("triggerSequence").ReadHttpInt64());
        var page = await client.GetFromJsonAsync<JsonElement>(Relative("/api/scheduling/tasks/"));
        var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task OneOccurrenceCannotCommit_LeavesNoPartialState_AndLaterPlansStillTrigger()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
        var broken = await CreatePlanAsync(client, "first-plan");
        var healthy = await CreatePlanAsync(client, "second-plan");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // 只对该临时库注入故障；结论仍从应用入口及 HTTP 查询观察。
        await using (var fail = new NpgsqlCommand($"ALTER TABLE scheduling.outbox ADD CONSTRAINT test_reject_plan CHECK ((\"Payload\"::jsonb ->> 'planId')::bigint <> {broken})", connection))
        {
            await fail.ExecuteNonQueryAsync();
        }
        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        var result = await runner.RunOnceAsync();
        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Triggered);
        Assert.Equal(broken, Assert.Single(result.FailedPlanIds));
        Assert.Empty(await HistoryAsync(client, broken));
        Assert.Single(await HistoryAsync(client, healthy));
        var page = await client.GetFromJsonAsync<JsonElement>(Relative("/api/scheduling/tasks/"));
        var plan = Assert.Single(page.GetProperty("data").EnumerateArray(), item => item.GetProperty("taskId").ReadHttpInt64() == broken);
        Assert.Equal(1, plan.GetProperty("version").ReadHttpInt64());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("lastRunAt").ValueKind);

        await using (var recover = new NpgsqlCommand("ALTER TABLE scheduling.outbox DROP CONSTRAINT test_reject_plan", connection))
        {
            await recover.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
        var recovered = Assert.Single(await HistoryAsync(client, broken));
        Assert.Equal(1, recovered.GetProperty("triggerSequence").ReadHttpInt64());
        Assert.Equal("Pending", recovered.GetProperty("deliveryState").GetString());
    }

    [PostgresFact]
    public async Task DuePlan_RecordsPendingOccurrence_AndDoesNotFireAgainBeforeNextTime()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
        var itemId = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
            new { code = "record-cost", intervalSeconds = 3600, targetKind = "costing.recalculate", targetId = itemId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();

        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        Assert.Equal(new ScheduleRunResult(1, 1, 0), await runner.RunOnceAsync());
        using var history = await client.GetAsync(Relative($"/api/scheduling/tasks/{id}/occurrences"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var page = await history.Content.ReadFromJsonAsync<JsonElement>();
        var occurrence = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.NotEqual(Guid.Empty, occurrence.GetProperty("occurrenceId").GetGuid());
        Assert.Equal(1, occurrence.GetProperty("triggerSequence").ReadHttpInt64());
        Assert.Equal("Pending", occurrence.GetProperty("deliveryState").GetString());
        Assert.Equal(itemId, occurrence.GetProperty("targetId").GetGuid());
        Assert.Equal(0, occurrence.GetProperty("attemptCount").GetInt32());
        var scheduledAt = occurrence.GetProperty("scheduledAt").GetDateTimeOffset();
        var triggeredAt = occurrence.GetProperty("triggeredAt").GetDateTimeOffset();
        Assert.True(triggeredAt >= scheduledAt);
        Assert.Equal(new ScheduleRunResult(0, 0, 0), await runner.RunOnceAsync());
        using var listed = await client.GetAsync(Relative("/api/scheduling/tasks/"));
        var plans = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var plan = Assert.Single(plans.GetProperty("data").EnumerateArray());
        Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
        Assert.Equal(triggeredAt.AddHours(1), plan.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<long> CreatePlanAsync(HttpClient client, string code)
    {
        using var created = await client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
            new { code, intervalSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
    }

    private static async Task<JsonElement[]> HistoryAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync(Relative($"/api/scheduling/tasks/{id}/occurrences"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("data").EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
