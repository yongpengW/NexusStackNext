using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed partial class PricingCacheTests
{
    [RedisPostgresFact]
    public async Task FactRecovery_PreservesCompletedQuote_TaskHistoryAndHotRedisValue_WithoutRenewingItsLifetime()
    {
        var settings = RedisSettings();
        settings["Pricing__Cache__Ttl"] = "00:05:00";
        await using var control = await ConnectionMultiplexer.ConnectAsync(settings["Pricing__Cache__ConnectionString"]);
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var key = settings["Pricing__Cache__Namespace"] + ":pricing:quote:v2:" + itemId.ToString("N");
        await using var host = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing",
            database.ConnectionString, worker: true, settings: settings);
        host.Authenticate();
        using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = taskId, itemId, expectedVersion = "0", cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await WaitForQuoteAsync(host, itemId, quote => quote.BreakEvenPrice == 100m && quote.InputRevision == quote.CalculatedRevision);
        await WaitForInvalidationsDrainedAsync(itemId);
        var cache = control.GetDatabase();
        var before = await WaitForCachedQuoteAsync(host, itemId, cache, key, quote => quote.BreakEvenPrice == 100m);
        var taskPath = new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative);
        var taskBefore = (await host.Client.GetFromJsonAsync<JsonElement>(taskPath)).GetProperty("data").Clone();
        Assert.Equal("Succeeded", taskBefore.GetProperty("state").GetString());
        Assert.Single(taskBefore.GetProperty("history").EnumerateArray());
        var value = await cache.HashGetAsync(key, "value");
        Assert.True(value.HasValue);
        var lifetime = await cache.KeyTimeToLiveAsync(key);
        Assert.NotNull(lifetime);
        Assert.True(lifetime > TimeSpan.FromMinutes(4));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddPricingPostgres(database.ConnectionString);
        await using var owner = builder.Build();
        await using var scope = owner.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        Assert.All(originals, entry => Assert.Equal(PriceQuoteCommittedV1.Name, entry.EventName));
        var target = originals[^1];
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var request = new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" };
        var path = new Uri($"/api/pricing/audit-deliveries/{target.Id}/retry", UriKind.Relative);
        using var recovered = await host.Client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var replay = await host.Client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals((await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data"),
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, pending.Id);
        Assert.Equal(target.Payload, pending.Payload);
        Assert.Equal(target.OccurredAt, pending.OccurredAt);
        Assert.Equal(before, await ReadQuoteAsync(host, itemId));
        Assert.True(JsonElement.DeepEquals(taskBefore, (await host.Client.GetFromJsonAsync<JsonElement>(taskPath)).GetProperty("data")));
        Assert.Equal(value, await cache.HashGetAsync(key, "value"));
        var afterLifetime = await cache.KeyTimeToLiveAsync(key);
        Assert.NotNull(afterLifetime);
        Assert.True(afterLifetime <= lifetime);
        await WaitForInvalidationsDrainedAsync(itemId);
        Assert.Equal(value, await cache.HashGetAsync(key, "value"));
    }
}
