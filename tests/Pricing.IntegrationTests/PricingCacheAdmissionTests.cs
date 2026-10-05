using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.PricingHost;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed partial class PricingCacheTests
{
    [RedisPostgresFact]
    public async Task LedgerContention_PreservesHotCacheAndLeavesNoInvalidationToReplay_AfterRestart()
    {
        var settings = RedisSettings();
        settings["Pricing__Cache__Ttl"] = "00:05:00";
        settings["Pricing__AuditDelivery__CapacityWrite__Timeout"] = "00:00:00.250";
        await using var control = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!);
        var item = Guid.NewGuid();
        var original = new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m);
        var change = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 96m };
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v2:" + item.ToString("N");
        PriceQuoteView before;
        await using (var first = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings))
        {
            first.Authenticate();
            using var accepted = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), original);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await WaitForInvalidationsDrainedAsync(item);
            before = await WaitForCachedQuoteAsync(first, item, control.GetDatabase(), key, quote => quote.Cost == 80m);
            await using var capacityLock = await PostgresFactCapacityLock.AcquireAsync(database.ConnectionString, "pricing");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var rejected = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), change, deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var rejectedFee = await first.Client.PostAsJsonAsync(new Uri("/api/pricing/fee", UriKind.Relative),
                new UpdatePricingFee(Guid.NewGuid(), item, 1, 0.3m), deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedFee.StatusCode);
            Assert.Equal("audit_capacity.busy", (await rejectedFee.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var missing = await first.Client.GetAsync(new Uri($"/api/pricing/tasks/{change.RequestId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            await capacityLock.ReleaseAsync();
        }
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, settings: settings);
        restarted.Authenticate();
        await WaitForInvalidationsDrainedAsync(item);
        try
        {
            // 不能让回源掩盖错误的失效待办；重启后仍必须能靠原热值服务。
            await database.SetAvailableAsync(false);
            Assert.Equal(before, await ReadQuoteAsync(restarted, item));
        }
        finally { await database.SetAvailableAsync(true); }
        using var recovered = await restarted.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), change);
        if (recovered.StatusCode != HttpStatusCode.Accepted)
        {
            var response = await recovered.Content.ReadFromJsonAsync<JsonElement>();
            var code = response.TryGetProperty("errorCode", out var error) ? error.GetString() : null;
            Assert.Fail($"[DEBUG-105] RecoveryWrite HTTP={(int)recovered.StatusCode}; "
                + $"CapacityBusy={code == "audit_capacity.busy"}; CapacityUnavailable={code == "audit_capacity.unavailable"}.");
        }
        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        var current = await WaitForCachedQuoteAsync(restarted, item, control.GetDatabase(), key, quote => quote.Cost == 96m);
        Assert.Equal(2, current.Version);
    }
}
