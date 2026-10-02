using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingPersistenceJourneyTests
{
    [PostgresFact]
    public async Task IndependentSchedulingMigration_IsRequired_AndDatabaseOutageChangesReadiness()
    {
        await using var platform = await IdentityJourneyDatabase.CreateAsync();
        await platform.MigrateAsync();
        await using var scheduling = await IdentityJourneyDatabase.CreateAsync();
        await using (var unprepared = new PersistentIdentityApp(platform.ConnectionString, schedulingConnectionString: scheduling.ConnectionString))
        {
            var error = Assert.ThrowsAny<Exception>(() => unprepared.CreateClient());
            Assert.Contains("Scheduling 数据库需要迁移", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(scheduling.ConnectionString, "Scheduling")).ExitCode);
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(scheduling.ConnectionString, "Scheduling")).ExitCode);
        await using var prepared = new PersistentIdentityApp(platform.ConnectionString, schedulingConnectionString: scheduling.ConnectionString);
        using var client = prepared.CreateClient();
        using var ready = await client.GetAsync(Relative("/health/ready"));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        try
        {
            await scheduling.SetAvailableAsync(false);
            using var unavailable = await client.GetAsync(Relative("/health/ready"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using var live = await client.GetAsync(Relative("/health/live"));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { await scheduling.SetAvailableAsync(true); }
        using var recovered = await client.GetAsync(Relative("/health/ready"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [PostgresFact]
    public async Task UnavailableSchedulingDatabase_RefusesStartupWithSanitizedDiagnostic()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString,
            schedulingConnectionString: "Host=127.0.0.1;Port=1;Database=unavailable;Timeout=1");
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Scheduling 数据库不可用", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Port=1", error.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Restart_PreservesTargetPauseVersionAndNextOccurrenceTime()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        long id;
        var itemId = Guid.NewGuid();
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "schedule-root-password");
            using var created = await first.Client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
                new { code = "persistent-cost", intervalSeconds = 3600, firstRunInSeconds = 3600, targetKind = "costing.recalculate", targetId = itemId });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").GetInt64();
            using var paused = await first.Client.PostAsJsonAsync(Relative($"/api/scheduling/tasks/{id}/pause"), new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
            await first.CrashAsync();
        }
        JsonElement resumed;
        await using (var second = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(second.Client, "journey-root", "schedule-root-password");
            var plan = await OnlyPlanAsync(second.Client);
            Assert.Equal(id, plan.GetProperty("taskId").GetInt64());
            Assert.Equal(itemId, plan.GetProperty("targetId").GetGuid());
            Assert.Equal("costing.recalculate", plan.GetProperty("targetKind").GetString());
            Assert.Equal(2, plan.GetProperty("version").GetInt64());
            Assert.False(plan.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("nextRunAt").ValueKind);
            using var enabled = await second.Client.PostAsJsonAsync(Relative($"/api/scheduling/tasks/{id}/resume"), new { expectedVersion = 2 });
            Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
            resumed = await OnlyPlanAsync(second.Client);
        }
        await using var third = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password");
        await PlatformSettingsAccessTests.LoginAsync(third.Client, "journey-root", "schedule-root-password");
        var after = await OnlyPlanAsync(third.Client);
        Assert.Equal(3, after.GetProperty("version").GetInt64());
        Assert.True(after.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(resumed.GetProperty("nextRunAt").GetDateTimeOffset(), after.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
    private static async Task<JsonElement> OnlyPlanAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(Relative("/api/scheduling/tasks/"));
        return Assert.Single(page.GetProperty("data").EnumerateArray()).Clone();
    }
}
