using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.PricingHost;
using Npgsql;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingCacheTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [RedisPostgresFact]
    public async Task CommittedFeeAndCalculatedResult_InvalidateQuotesAcrossProcesses()
    {
        var settings = RedisSettings();
        await using var writer = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true, settings: settings);
        await using var reader = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        writer.Authenticate(); reader.Authenticate();
        var itemId = Guid.NewGuid();
        using var accepted = await writer.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var initial = await WaitForQuoteAsync(reader, itemId, x => x.BreakEvenPrice == 100m && x.CalculatedRevision == x.InputRevision);
        using var fee = await writer.Client.PostAsJsonAsync(new Uri("/api/pricing/fee", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = initial.Version, feeRate = 0.5m });
        Assert.Equal(HttpStatusCode.Accepted, fee.StatusCode);
        var current = await WaitForQuoteAsync(reader, itemId, x => x.BreakEvenPrice == 160m && x.CalculatedRevision == x.InputRevision);
        Assert.Equal(2, current.InputRevision);
        Assert.Equal(80m, current.Cost);
        // 热缓存同样经过业务授权。
        reader.Client.DefaultRequestHeaders.Authorization = null;
        using var denied = await reader.Client.GetAsync(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [RedisPostgresFact]
    public async Task CacheOutageThenProcessRestart_ReplaysCommittedInvalidations_AndKeepsTasks()
    {
        var settings = RedisSettings();
        await using var proxy = new RedisFaultProxy(settings["Pricing__Cache__ConnectionString"]);
        settings["Pricing__Cache__ConnectionString"] = proxy.ConnectionString;
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using var control = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!);
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v1:" + itemId.ToString("N");
        await using (var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
        {
            app.Authenticate();
            using var initial = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);
            await WaitForInvalidationsDrainedAsync(itemId);
            var quote = await WaitForCachedQuoteAsync(app, itemId, control.GetDatabase(), key, x => x.Cost == 80m);
            proxy.SetOffline(true);
            using var update = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId = taskId, itemId, expectedVersion = quote.Version, cost = 96m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, update.StatusCode);
            Assert.Equal(96m, (await ReadQuoteAsync(app, itemId)).Cost);
        }
        proxy.SetOffline(false);
        var stale = await control.GetDatabase().HashGetAsync(key, "value");
        Assert.True(stale.HasValue, "恢复前必须仍有旧缓存，不能由 TTL 到期代替可靠失效。");
        Assert.Equal(80m, JsonSerializer.Deserialize<PriceQuoteView>((string)stale!)!.Cost);
        // 先禁止计算产生新的失效意图：必须靠停机前那笔提交清掉旧缓存。
        await using (var replayOnly = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
        {
            replayOnly.Authenticate();
            var replayed = await WaitForCachedQuoteAsync(replayOnly, itemId, control.GetDatabase(), key, x => x.Cost == 96m);
            Assert.Null(replayed.BreakEvenPrice);
            var pending = await replayOnly.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
            Assert.Equal("Pending", pending.GetProperty("data").GetProperty("state").GetString());
            try
            {
                await database.SetAvailableAsync(false);
                Assert.Equal(96m, (await ReadQuoteAsync(replayOnly, itemId)).Cost);
            }
            finally { await database.SetAvailableAsync(true); }
        }
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true, settings: settings);
        restarted.Authenticate();
        var result = await WaitForQuoteAsync(restarted, itemId, x => x.Cost == 96m && x.BreakEvenPrice == 120m && x.InputRevision == x.CalculatedRevision);
        var task = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
        Assert.Equal("Succeeded", task.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(2, result.InputRevision);
    }

    [RedisPostgresFact]
    public async Task LateOldFill_AfterInvalidationCannotOverwriteTheNewQuote()
    {
        var settings = RedisSettings();
        settings["Pricing__Cache__RedisTimeout"] = "00:00:02";
        await using var proxy = new RedisFaultProxy(settings["Pricing__Cache__ConnectionString"]);
        var slowSettings = new Dictionary<string, string>(settings) { ["Pricing__Cache__ConnectionString"] = proxy.ConnectionString };
        await using var slow = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: slowSettings);
        await using var fast = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        slow.Authenticate(); fast.Authenticate();
        var itemId = Guid.NewGuid();
        using var accepted = await fast.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = await ReadQuoteAsync(fast, itemId);
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Pricing__Cache__ConnectionString"]);
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v1:" + itemId.ToString("N");
        // 仅清本次测试自己的键，模拟缓存丢失；不清空共享 Redis。
        await control.GetDatabase().KeyDeleteAsync(key);
        proxy.PauseNextFill();
        var oldRead = ReadQuoteAsync(slow, itemId);
        await proxy.WaitForPausedFillAsync();
        try
        {
            using var update = await fast.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId = Guid.NewGuid(), itemId, expectedVersion = original.Version, cost = 96m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, update.StatusCode);
            await WaitForCachedQuoteAsync(fast, itemId, control.GetDatabase(), key, x => x.Cost == 96m);
            Assert.False(oldRead.IsCompleted, "旧回填必须仍在等待，不能让客户端超时代替竞态验证。");
        }
        finally { proxy.ReleaseFill(); }
        Assert.Equal(80m, (await oldRead).Cost);
        Assert.Equal(96m, (await ReadQuoteAsync(fast, itemId)).Cost);
    }

    [RedisPostgresFact]
    public async Task EvictedQuote_RebuildsFromDatabase_WithoutLosingItsTask()
    {
        var settings = RedisSettings();
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        app.Authenticate();
        var itemId = Guid.NewGuid(); var taskId = Guid.NewGuid();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = taskId, itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(80m, (await ReadQuoteAsync(app, itemId)).Cost);
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Pricing__Cache__ConnectionString"]);
        await control.GetDatabase().KeyDeleteAsync(settings["Pricing__Cache__Namespace"] + ":pricing:quote:v1:" + itemId.ToString("N"));
        Assert.Equal(80m, (await ReadQuoteAsync(app, itemId)).Cost);
        var task = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
        Assert.Equal("Pending", task.GetProperty("data").GetProperty("state").GetString());
    }

    [RedisPostgresFact]
    public async Task WarmQuote_IsSharedWithASecondProcess_WithoutDatabaseAccess()
    {
        var settings = RedisSettings();
        // 第二进程可能尚未建立 Redis 连接；共享命中验收为冷连接预留预算。
        settings["Pricing__Cache__RedisTimeout"] = "00:00:02";
        await using var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        first.Authenticate();
        var itemId = Guid.NewGuid();
        using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = Guid.NewGuid(), itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        // 观察预热完成再停库；故障后的业务结论仍从第二进程的 HTTP 读取。
        await WaitForInvalidationsDrainedAsync(itemId);
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Pricing__Cache__ConnectionString"]);
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v1:" + itemId.ToString("N");
        await WaitForCachedQuoteAsync(first, itemId, control.GetDatabase(), key, x => x.Cost == 80m);
        // 预热后才启动读者，避免它持有已被另一实例确认、但尚未执行的重复失效。
        await using var second = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        second.Authenticate();
        try
        {
            await database.SetAvailableAsync(false);
            Assert.True(await control.GetDatabase().HashExistsAsync(key, "value"), "停库时必须仍有可供共享的缓存。");
            Assert.Equal(80m, (await ReadQuoteAsync(first, itemId)).Cost);
            Assert.Equal(80m, (await ReadQuoteAsync(second, itemId)).Cost);
        }
        finally { await database.SetAvailableAsync(true); }
    }

    private static Dictionary<string, string> RedisSettings() => new()
    {
        ["Pricing__Cache__Enabled"] = "true",
        ["Pricing__Cache__ConnectionString"] = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!,
        ["Pricing__Cache__Namespace"] = "nsn-test-" + Guid.NewGuid().ToString("N"),
        ["Pricing__Cache__Ttl"] = "00:01:00",
    };

    private static async Task<PriceQuoteView> ReadQuoteAsync(BusinessProcess app, Guid itemId)
    {
        var response = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative));
        return response.GetProperty("data").Deserialize<PriceQuoteView>(JsonSerializerOptions.Web)!;
    }

    private static async Task<PriceQuoteView> WaitForQuoteAsync(BusinessProcess app, Guid itemId, Func<PriceQuoteView, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var quote = await ReadQuoteAsync(app, itemId);
            if (matches(quote)) { return quote; }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task<PriceQuoteView> WaitForCachedQuoteAsync(BusinessProcess app, Guid itemId,
        IDatabase cache, RedisKey key, Func<PriceQuoteView, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var quote = await ReadQuoteAsync(app, itemId);
            // Redis 观察只定位故障时序，不以客户端调用次数代替 HTTP 业务断言。
            var payload = await cache.HashGetAsync(key, "value");
            if (matches(quote) && payload.HasValue && matches(JsonSerializer.Deserialize<PriceQuoteView>((string)payload!)!)) { return quote; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task WaitForInvalidationsDrainedAsync(Guid itemId)
    {
        // 故障前排空初始创建的失效，避免它碰巧替后续更新清缓存。
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pricing.cache_invalidations WHERE \"ItemId\" = @id)", connection);
            command.Parameters.AddWithValue("id", itemId);
            if (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    [PostgresFact]
    public async Task CacheOutage_BoundsDatabaseFallback_WhileAcceptedTasksRemainQueryable()
    {
        var settings = new Dictionary<string, string>
        {
            ["Pricing__Cache__Enabled"] = "true",
            ["Pricing__Cache__ConnectionString"] = "127.0.0.1:1",
            ["Pricing__Cache__Namespace"] = "test-" + Guid.NewGuid().ToString("N"),
            ["Pricing__Cache__MaxConcurrentLoads"] = "1",
        };
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        app.Authenticate();
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = taskId, itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE pricing.quotes IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }
        var first = app.Client.GetAsync(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative));
        Task<HttpResponseMessage>? second = null;
        bool bounded;
        try
        {
            await WaitForBlockedQueryAsync();
            second = app.Client.GetAsync(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative));
            bounded = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2))) == second;
            var task = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
            Assert.Equal("Pending", task.GetProperty("data").GetProperty("state").GetString());
        }
        finally { await transaction.RollbackAsync(); }
        using var firstResponse = await first;
        using var secondResponse = await second!;
        Assert.True(bounded, "回源已满时应有界返回，而不是再向数据库排队。");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    private async Task WaitForBlockedQueryAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%quotes%')", connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(20, timeout.Token);
        }
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
