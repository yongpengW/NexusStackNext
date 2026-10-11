using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using Npgsql;
using StackExchange.Redis;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingCacheTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task FullDisplayLoadBudget_RejectsAdditionalReadsWith503_AndTimeoutReleasesItsSlot()
    {
        var settings = new Dictionary<string, string>
        {
            ["Costing__Cache__MaxConcurrentLoads"] = "1",
            ["Costing__Cache__LoadTimeout"] = "00:00:02",
        };
        await using var app = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
        app.Authenticate();
        var itemId = Guid.NewGuid();
        await AcceptAsync(app, itemId, 0, 80m);
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE costing.sheets IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        { await command.ExecuteNonQueryAsync(); }
        var first = app.Client.GetAsync(Relative($"/api/costing/items/{itemId}/display"));
        try
        {
            await WaitForBlockedLoadAsync();
            using var second = await app.Client.GetAsync(Relative($"/api/costing/items/{itemId}/display"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            Assert.Equal("costing.query_busy", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var timedOut = await first;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
            Assert.Equal("costing.query_timeout", (await timedOut.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
        finally { await transaction.RollbackAsync(); using var drained = await first; }
        Assert.Equal(80m, (await ReadSheetAsync(app, itemId, display: true)).PurchaseCost);
    }

    [RedisPostgresFact]
    public async Task InputAndResultCommits_InvalidateDisplay_WhileRollbackAndNoOpPreserveTheHotValue()
    {
        var settings = RedisSettings();
        var itemId = Guid.NewGuid();
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Costing__Cache__ConnectionString"]);
        var key = CacheKey(settings, itemId);
        await using var app = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
        app.Authenticate();
        await AcceptAsync(app, itemId, 0, 80m);
        await WaitForInvalidationDrainAsync(itemId);
        var original = await WaitForCachedAsync(app, itemId, control.GetDatabase(), key, sheet => sheet.PurchaseCost == 80m);

        var failedRequest = Guid.NewGuid();
        await using var commands = CreateApplication();
        await using var scope = commands.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var reject = new NpgsqlCommand("""
                CREATE FUNCTION costing.reject_display_test() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected task failure'; END $$;
                CREATE TRIGGER reject_display_test BEFORE INSERT ON costing.tasks
                FOR EACH ROW EXECUTE FUNCTION costing.reject_display_test();
                """, connection);
            await reject.ExecuteNonQueryAsync();
            try
            {
                await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(
                    new UpdateCostInputs(failedRequest, itemId, original.Version, 96m, 20m)));
                Assert.Equal(original, await ReadSheetAsync(app, itemId, display: false));
                Assert.Equal(original, await ReadSheetAsync(app, itemId, display: true));
                Assert.Equal(0L, await PendingInvalidationsAsync(connection, itemId));
            }
            finally
            {
                await using var restore = new NpgsqlCommand("DROP TRIGGER reject_display_test ON costing.tasks", connection);
                await restore.ExecuteNonQueryAsync();
            }
        }
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), itemId, original.Version, 80m, 20m))).IsSuccess);
        Assert.Equal(original, await ReadSheetAsync(app, itemId, display: true));
        await AcceptAsync(app, itemId, original.Version, 96m);
        var updated = await WaitForCachedAsync(app, itemId, control.GetDatabase(), key, sheet => sheet.PurchaseCost == 96m);
        Assert.Equal(2, updated.Version);
        while ((await sender.SendAsync(new ClaimCostingWork())).Value is { } lease)
        { Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value); }
        var calculated = await WaitForCachedAsync(app, itemId, control.GetDatabase(), key, sheet => sheet.UnitCost == 116m);
        Assert.Equal(2, calculated.CalculatedRevision);
        Assert.Equal(calculated, await ReadSheetAsync(app, itemId, display: false));
        app.Client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await app.Client.GetAsync(Relative($"/api/costing/items/{itemId}/display"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        app.Authenticate(root: false);
        using var forbidden = await app.Client.GetAsync(Relative($"/api/costing/items/{itemId}/display"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [RedisPostgresFact]
    public async Task RedisOutageAndRestart_ReplayOriginalInvalidation_AndStrongReadNeverUsesStaleDisplay()
    {
        var settings = RedisSettings();
        await using var proxy = new RedisFaultProxy(settings["Costing__Cache__ConnectionString"]);
        settings["Costing__Cache__ConnectionString"] = proxy.ConnectionString;
        var itemId = Guid.NewGuid();
        await using var control = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!);
        var key = CacheKey(settings, itemId);
        await using (var first = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings))
        {
            first.Authenticate();
            await AcceptAsync(first, itemId, 0, 80m);
            await WaitForInvalidationDrainAsync(itemId);
            var original = await WaitForCachedAsync(first, itemId, control.GetDatabase(), key, sheet => sheet.PurchaseCost == 80m);
            proxy.SetOffline(true);
            await AcceptAsync(first, itemId, original.Version, 96m);
            Assert.Equal(96m, (await ReadSheetAsync(first, itemId, display: false)).PurchaseCost);
            Assert.Equal(96m, (await ReadSheetAsync(first, itemId, display: true)).PurchaseCost);
        }
        var stale = await control.GetDatabase().HashGetAsync(key, "value");
        Assert.True(stale.HasValue, "必须保留旧值，不能让 TTL 代替重启后的失效恢复。");
        Assert.Equal(80m, JsonSerializer.Deserialize<CostSheetView>((string)stale!)!.PurchaseCost);
        proxy.SetOffline(false);
        await using var restarted = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
        restarted.Authenticate();
        var replayed = await WaitForCachedAsync(restarted, itemId, control.GetDatabase(), key, sheet => sheet.PurchaseCost == 96m);
        Assert.Null(replayed.UnitCost);
        using var tasks = await restarted.Client.GetAsync(Relative("/api/costing/tasks"));
        Assert.Equal(HttpStatusCode.OK, tasks.StatusCode);
        Assert.Equal(2, (await tasks.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetArrayLength());
        await control.GetDatabase().KeyDeleteAsync(key);
        Assert.Equal(replayed, await ReadSheetAsync(restarted, itemId, display: true));
    }

    [RedisPostgresFact]
    public async Task LateOldFill_AfterCommittedInvalidationCannotReplaceCurrentDisplay()
    {
        var settings = RedisSettings();
        settings["Costing__Cache__RedisTimeout"] = "00:00:02";
        await using var proxy = new RedisFaultProxy(settings["Costing__Cache__ConnectionString"]);
        var slowSettings = new Dictionary<string, string>(settings) { ["Costing__Cache__ConnectionString"] = proxy.ConnectionString };
        await using var fast = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: settings);
        fast.Authenticate();
        var itemId = Guid.NewGuid();
        await AcceptAsync(fast, itemId, 0, 80m);
        await WaitForInvalidationDrainAsync(itemId);
        await using var slow = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, settings: slowSettings);
        slow.Authenticate();
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Costing__Cache__ConnectionString"]);
        var key = CacheKey(settings, itemId);
        proxy.PauseNextFill();
        var oldRead = ReadSheetAsync(slow, itemId, display: true);
        await proxy.WaitForPausedFillAsync();
        try
        {
            await AcceptAsync(fast, itemId, 1, 96m);
            await WaitForCachedAsync(fast, itemId, control.GetDatabase(), key, sheet => sheet.PurchaseCost == 96m);
            Assert.False(oldRead.IsCompleted, "旧填充必须仍暂停，不能以超时替代竞态。");
        }
        finally { proxy.ReleaseFill(); }
        Assert.Equal(80m, (await oldRead).PurchaseCost);
        Assert.Equal(96m, (await ReadSheetAsync(fast, itemId, display: true)).PurchaseCost);
    }

    [PostgresFact]
    public async Task DisplayQuery_WithRedisOutage_FallsBackWithoutChangingAuthoritativeRead()
    {
        var settings = new Dictionary<string, string>
        {
            ["Costing__Cache__Enabled"] = "true",
            ["Costing__Cache__ConnectionString"] = "127.0.0.1:1",
            ["Costing__Cache__Namespace"] = "nsn-test-" + Guid.NewGuid().ToString("N"),
            ["Costing__Cache__MaxConcurrentLoads"] = "1",
        };
        await using var app = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString,
            worker: false, settings: settings);
        app.Authenticate();
        var itemId = Guid.NewGuid();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, purchaseCost = 80m, freightCost = 20m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var display = await app.Client.GetAsync(new Uri($"/api/costing/items/{itemId}/display", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);
        Assert.Equal(80m, (await ReadAsync(display)).GetProperty("purchaseCost").GetDecimal());

        using var current = await app.Client.GetAsync(new Uri($"/api/costing/items/{itemId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(1L, (await ReadAsync(current)).GetProperty("version").ReadHttpInt64());
    }

    [RedisPostgresFact]
    public async Task DisplaySnapshot_IsSharedAcrossProcesses_AndSurvivesDatabaseOutage()
    {
        var settings = RedisSettings();
        var itemId = Guid.NewGuid();
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Costing__Cache__ConnectionString"]);
        var key = settings["Costing__Cache__Namespace"] + ":costing:sheet:v1:" + itemId.ToString("N");
        await using var first = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString,
            worker: false, settings: settings);
        first.Authenticate();
        using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, purchaseCost = 80m, freightCost = 20m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await WaitForInvalidationDrainAsync(itemId);
        using var display = await first.Client.GetAsync(new Uri($"/api/costing/items/{itemId}/display", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);
        await WaitForCacheValueAsync(control.GetDatabase(), key);

        await using var second = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString,
            worker: false, settings: settings);
        second.Authenticate();
        try
        {
            await database.SetAvailableAsync(false);
            using var shared = await second.Client.GetAsync(new Uri($"/api/costing/items/{itemId}/display", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
            Assert.Equal(80m, (await ReadAsync(shared)).GetProperty("purchaseCost").GetDecimal());
            using var authoritative = await second.Client.GetAsync(new Uri($"/api/costing/items/{itemId}", UriKind.Relative));
            Assert.NotEqual(HttpStatusCode.OK, authoritative.StatusCode);
        }
        finally { await database.SetAvailableAsync(true); }
    }

    private static Dictionary<string, string> RedisSettings() => new()
    {
        ["Costing__Cache__Enabled"] = "true",
        ["Costing__Cache__ConnectionString"] = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!,
        ["Costing__Cache__Namespace"] = "nsn-test-" + Guid.NewGuid().ToString("N"),
        ["Costing__Cache__Ttl"] = "00:01:00",
    };

    private ServiceProvider CreateApplication()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
    private static string CacheKey(Dictionary<string, string> settings, Guid itemId) => settings["Costing__Cache__Namespace"] + ":costing:sheet:v1:" + itemId.ToString("N");
    private static async Task AcceptAsync(BusinessProcess app, Guid itemId, long version, decimal purchase)
    {
        using var response = await app.Client.PostAsJsonAsync(Relative("/api/costing/cost"),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = version, purchaseCost = purchase, freightCost = 20m });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<CostSheetView> ReadSheetAsync(BusinessProcess app, Guid itemId, bool display)
    {
        using var response = await app.Client.GetAsync(Relative($"/api/costing/items/{itemId}" + (display ? "/display" : string.Empty)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).Deserialize<CostSheetView>(JsonSerializerOptions.Web)!;
    }

    private static async Task<CostSheetView> WaitForCachedAsync(BusinessProcess app, Guid itemId, IDatabase cache, RedisKey key, Func<CostSheetView, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var value = await ReadSheetAsync(app, itemId, display: true);
            var payload = await cache.HashGetAsync(key, "value");
            if (matches(value) && payload.HasValue && matches(JsonSerializer.Deserialize<CostSheetView>((string)payload!)!)) { return value; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task WaitForBlockedLoadAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%sheets%')", connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<long> PendingInvalidationsAsync(NpgsqlConnection connection, Guid itemId)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM costing.cache_invalidations WHERE \"ItemId\" = @id", connection);
        command.Parameters.AddWithValue("id", itemId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task WaitForInvalidationDrainAsync(Guid itemId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM costing.cache_invalidations WHERE \"ItemId\" = @id)", connection);
            command.Parameters.AddWithValue("id", itemId);
            if (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WaitForCacheValueAsync(IDatabase cache, RedisKey key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await cache.HashExistsAsync(key, "value")) { await Task.Delay(20, timeout.Token); }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data");
    }
}

internal sealed class RedisPostgresFactAttribute : FactAttribute
{
    public RedisPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")))
        { Skip = "需要独立测试 PostgreSQL 与 Redis；CI 必须配置两者。"; }
    }
}
