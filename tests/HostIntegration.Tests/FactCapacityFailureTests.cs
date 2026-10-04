using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityFailureTests
{
    [PostgresFact]
    public async Task SourceDatabaseOutage_ReturnsHttp503_WhenTheIdentityAuthorityRemainsAvailable()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateAsync();
        await using var source = await IdentityJourneyDatabase.CreateAsync();
        await using (var schema = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(source.ConnectionString, PlatformDbContext.SchemaName).Options))
        { await schema.Database.MigrateAsync(); }
        await using var app = new BudgetApp(identity.ConnectionString, source.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-failure-password");
        var path = new Uri("/api/platform/audit-capacity", UriKind.Relative);
        using var before = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var snapshot = await before.Content.ReadApiDataAsync();
        await source.SetAvailableAsync(false);
        try
        {
            using var unavailable = await client.GetAsync(path);
            await AssertUnavailableAsync(unavailable);
            using var authority = await client.GetAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, authority.StatusCode);
        }
        finally { await source.SetAvailableAsync(true); }
        using var recovered = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(snapshot.GetRawText(), (await recovered.Content.ReadApiDataAsync()).GetRawText());
    }

    [PostgresFact]
    public async Task CallerCancellationAndDatabaseOutage_PreserveFailureSemantics_AndRecover()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new BudgetApp(database.ConnectionString);
        await using var scope = app.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        var initial = await reader.ReadAsync();
        Assert.True(initial.IsSuccess);
        await using (var blocker = new NpgsqlConnection(database.ConnectionString))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("LOCK TABLE platform.fact_capacity IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await command.ExecuteNonQueryAsync();
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
            }
            finally { await transaction.RollbackAsync(); }
        }
        Assert.Equal(initial.Value, (await reader.ReadAsync()).Value);
        await database.SetAvailableAsync(false);
        try
        {
            var watch = Stopwatch.StartNew();
            var unavailable = await reader.ReadAsync();
            Assert.True(unavailable.IsFailure);
            Assert.Equal(CommittedFactCapacityErrors.Unavailable, unavailable.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "故障连接不能落入业务命令的执行重试预算。");
        }
        finally { await database.SetAvailableAsync(true); }
        Assert.Equal(initial.Value, (await reader.ReadAsync()).Value);
    }

    [PostgresFact]
    public async Task LockedLedger_UsesShortBudget_AndDoesNotLeaveAServerQueryWaiting()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var readerName = "capacity-budget-" + Guid.NewGuid().ToString("N");
        var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = readerName };
        await using var app = new BudgetApp(options.ConnectionString);
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-failure-password");
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE platform.fact_capacity IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        { await command.ExecuteNonQueryAsync(); }
        try
        {
            var watch = Stopwatch.StartNew();
            using var unavailable = await client.GetAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative));
            await AssertUnavailableAsync(unavailable);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "容量读取必须使用配置的 250ms 预算，而非默认重试等待。");
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            await using var activity = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE application_name = @name AND state = 'active' AND query LIKE '%fact_capacity%'
                """, observer);
            activity.Parameters.AddWithValue("name", readerName);
            var cleanupWatch = Stopwatch.StartNew();
            long remaining;
            do
            {
                remaining = (long)(await activity.ExecuteScalarAsync())!;
                if (remaining == 0) { break; }
                await Task.Delay(25);
            } while (cleanupWatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(0, remaining);
        }
        finally { await transaction.RollbackAsync(); }
        using var recovered = await client.GetAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, (await recovered.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task MissingLedger_ReturnsSafe503_AndRestoredLedgerRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new BudgetApp(database.ConnectionString);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-failure-password");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var owner in new[] { "platform", "identity", "files", "scheduling" })
        {
            var path = new Uri($"/api/{owner}/audit-capacity", UriKind.Relative);
            using var before = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            var snapshot = await before.Content.ReadApiDataAsync();
            await using (var hide = new NpgsqlCommand($"ALTER TABLE {owner}.fact_capacity RENAME TO unavailable_capacity", connection))
            { await hide.ExecuteNonQueryAsync(); }
            try
            {
                using var missing = await client.GetAsync(path);
                await AssertUnavailableAsync(missing);
            }
            finally
            {
                await using var restore = new NpgsqlCommand($"ALTER TABLE {owner}.unavailable_capacity RENAME TO fact_capacity", connection);
                await restore.ExecuteNonQueryAsync();
            }
            using var after = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            Assert.Equal(snapshot.GetRawText(), (await after.Content.ReadApiDataAsync()).GetRawText());
        }
        await using (var removeRow = new NpgsqlCommand("DELETE FROM platform.fact_capacity", connection))
        { Assert.Equal(1, await removeRow.ExecuteNonQueryAsync()); }
        using var missingRow = await client.GetAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative));
        await AssertUnavailableAsync(missingRow);
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("audit_capacity.unavailable", error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("Npgsql", error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
        Assert.False(error.TryGetProperty("retainedRecords", out _));
    }

    private sealed class BudgetApp(string connectionString, string? platformConnectionString = null) : PersistentIdentityApp(connectionString,
        "capacity-failure-password", platformConnectionString: platformConnectionString, schedulingWorkerEnabled: false)
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new[] { "Platform", "Identity", "Files", "Scheduling" }
                    .SelectMany(owner => new Dictionary<string, string?>
                    {
                        [$"{owner}:AuditDelivery:CapacityRead:Timeout"] = "00:00:00.250",
                        [$"{owner}:AuditDelivery:Cleanup:Enabled"] = "false",
                    })));
            return base.CreateHost(builder);
        }
    }
}
