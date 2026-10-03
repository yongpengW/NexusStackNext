using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.PricingHost;
using Npgsql;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed partial class PricingCacheTests
{
    [RedisPostgresFact]
    public async Task CapacityRefusal_PreservesHotCacheAcrossRestart_AndRecoveryKeepsInvalidationsDurableThroughRedisOutage()
    {
        var settings = RedisSettings();
        settings["Pricing__Cache__Ttl"] = "00:05:00";
        await using var proxy = new RedisFaultProxy(settings["Pricing__Cache__ConnectionString"]);
        settings["Pricing__Cache__ConnectionString"] = proxy.ConnectionString;
        await using var control = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!);
        var item = Guid.NewGuid();
        var original = new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m);
        var refused = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 96m };
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v2:" + item.ToString("N");
        PriceQuoteView before;
        try
        {
            await SetFactQuotaAsync(1);
            await using (var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
            {
                first.Authenticate();
                using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), original);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                await WaitForInvalidationsDrainedAsync(item);
                before = await WaitForCachedQuoteAsync(first, item, control.GetDatabase(), key, quote => quote.Cost == 80m);
                using var rejected = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), refused);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Equal("pricing.audit_capacity_exhausted", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
                using var rejectedFee = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/fee", UriKind.Relative),
                    new UpdatePricingFee(Guid.NewGuid(), item, 1, 0.3m));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedFee.StatusCode);
                using var missingTask = await first.Client.GetAsync(new Uri($"/api/pricing/tasks/{refused.RequestId}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NotFound, missingTask.StatusCode);
            }
            await using (var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
            {
                restarted.Authenticate();
                // 若拒绝事务留下失效待办，重启后的投递器会清掉热值；回源故障时公共查询就会失败。
                await WaitForInvalidationsDrainedAsync(item);
                try
                {
                    await database.SetAvailableAsync(false);
                    Assert.Equal(before, await ReadQuoteAsync(restarted, item));
                }
                finally { await database.SetAvailableAsync(true); }

                await SetFactQuotaAsync(2);
                proxy.SetOffline(true);
                using var recovered = await restarted.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), refused);
                Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
                Assert.Equal(96m, (await ReadQuoteAsync(restarted, item)).Cost);
            }
            proxy.SetOffline(false);
            // 保留本测试热值作为故障时序证据，防止 TTL 或其他初始化碰巧代替提交后的持久失效。
            var stale = await control.GetDatabase().HashGetAsync(key, "value");
            Assert.True(stale.HasValue);
            Assert.Equal(80m, JsonSerializer.Deserialize<PriceQuoteView>((string)stale!)!.Cost);
            await using var replay = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
            replay.Authenticate();
            var current = await WaitForCachedQuoteAsync(replay, item, control.GetDatabase(), key, quote => quote.Cost == 96m);
            Assert.Equal(2, current.Version);
            Assert.Equal(2, current.InputRevision);
            using var task = await replay.Client.GetAsync(new Uri($"/api/pricing/tasks/{refused.RequestId}", UriKind.Relative));
            Assert.Equal("Pending", (await task.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("state").GetString());
            try
            {
                await database.SetAvailableAsync(false);
                Assert.Equal(current, await ReadQuoteAsync(replay, item));
            }
            finally { await database.SetAvailableAsync(true); }
        }
        finally
        {
            // 本类复用隔离夹具；只还原本测试临时策略，后续用例仍以默认策略执行。
            await SetFactQuotaAsync(100000);
        }
    }

    private async Task SetFactQuotaAsync(long records)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE pricing.fact_capacity SET \"MaxRecords\" = @records", connection);
        command.Parameters.AddWithValue("records", records);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
