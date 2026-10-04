using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingFactCapacityPolicyCacheTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>
{
    [RedisPostgresFact]
    public async Task PolicyAdjustment_PreservesHotQuoteAndTask_WhenQuoteStorageIsUnavailable()
    {
        var settings = new Dictionary<string, string>
        {
            ["Pricing__Cache__Enabled"] = "true",
            ["Pricing__Cache__ConnectionString"] = Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_REDIS")!,
            ["Pricing__Cache__Namespace"] = "nsn-test-" + Guid.NewGuid().ToString("N"),
            ["Pricing__Cache__Ttl"] = "00:01:00"
        };
        await using var host = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location,
            "Pricing", database.ConnectionString, settings: settings);
        host.Authenticate();
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { requestId = taskId, itemId, expectedVersion = "0", cost = 80m, feeRate = 0.2m });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        // Arrange the fault only after the initial business invalidation has finished.
        // This query synchronizes the fixture; cache behavior is asserted solely through HTTP.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            await using var pending = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pricing.cache_invalidations WHERE \"ItemId\" = @id)", connection);
            pending.Parameters.AddWithValue("id", itemId);
            if (!(bool)(await pending.ExecuteScalarAsync(deadline.Token))!) { break; }
            await Task.Delay(20, deadline.Token);
        }
        var quotePath = new Uri($"/api/pricing/items/{itemId}", UriKind.Relative);
        using var warm = await host.Client.GetAsync(quotePath);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        var originalQuote = (await warm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText();
        using var originalTaskResponse = await host.Client.GetAsync(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, originalTaskResponse.StatusCode);
        var originalTask = (await originalTaskResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText();
        var policyPath = new Uri("/api/pricing/audit-capacity", UriKind.Relative);
        using var initialResponse = await host.Client.GetAsync(policyPath);
        var initial = (await initialResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, initial.GetProperty("maxRecords").ReadHttpInt64() + 1,
            initial.GetProperty("maxPayloadBytes").ReadHttpInt64(), initial.GetProperty("maxRecordPayloadBytes").GetInt32(), "operator-adjustment");
        await using (var hide = new NpgsqlCommand("ALTER TABLE pricing.quotes RENAME TO policy_test_unavailable_quotes", connection)
        { CommandTimeout = 2 })
        {
            await hide.ExecuteNonQueryAsync();
        }
        try
        {
            using var cacheOnly = await host.Client.GetAsync(quotePath);
            Assert.Equal(HttpStatusCode.OK, cacheOnly.StatusCode);
            Assert.Equal(originalQuote, (await cacheOnly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
            using var adjusted = await host.Client.PutAsJsonAsync(policyPath, request);
            Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
            Assert.Equal(2, (await adjusted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("policyRevision").ReadHttpInt64());
            using var stillCached = await host.Client.GetAsync(quotePath);
            Assert.Equal(HttpStatusCode.OK, stillCached.StatusCode);
            Assert.Equal(originalQuote, (await stillCached.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
            using var task = await host.Client.GetAsync(new Uri($"/api/pricing/tasks/{taskId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, task.StatusCode);
            Assert.Equal(originalTask, (await task.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
            using var afterResponse = await host.Client.GetAsync(policyPath);
            var after = (await afterResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(1, after.GetProperty("retainedRecords").ReadHttpInt64());
            Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        }
        finally
        {
            await using var restore = new NpgsqlCommand("ALTER TABLE pricing.policy_test_unavailable_quotes RENAME TO quotes", connection)
            { CommandTimeout = 2 };
            await restore.ExecuteNonQueryAsync();
        }
    }
}
