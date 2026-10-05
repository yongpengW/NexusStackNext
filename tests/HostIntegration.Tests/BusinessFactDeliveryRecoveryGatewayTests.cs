using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class BusinessFactDeliveryRecoveryGatewayTests
{
    [PostgresFact]
    public Task CostingPostgres_BusinessRoutes_RequireRootAndKeepRecoveryIdentityTrusted()
        => VerifyAsync("costing", "routes.business.json");

    [PostgresFact]
    public Task PricingPostgres_PricingRoutes_RequireRootAndKeepRecoveryIdentityTrusted()
        => VerifyAsync("pricing", "routes.pricing.json");

    [PostgresFact]
    public Task PricingPostgres_BusinessRoutes_RequireRootAndKeepRecoveryIdentityTrusted()
        => VerifyAsync("pricing", "routes.business.json");

    private static async Task VerifyAsync(string source, string configurationFile)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        if (source == "costing") { await CostingDatabase.MigrateAsync(database.ConnectionString); }
        else { await PricingDatabase.MigrateAsync(database.ConnectionString); }
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", database.ConnectionString);
        await using var reader = source == "costing"
            ? TaskOperationTests.CreateCostingApp(database.ConnectionString, null)
            : TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await using var scope = reader.Services.CreateAsyncScope();
        host.Authenticate();
        var policyPath = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initialResponse = await host.Client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        using var changed = await host.Client.PutAsJsonAsync(policyPath, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = initial.GetProperty("policyRevision").GetString(),
            maxRecords = initial.GetProperty("maxRecords").ReadHttpInt64() + 1,
            maxPayloadBytes = initial.GetProperty("maxPayloadBytes").GetString(),
            maxRecordPayloadBytes = initial.GetProperty("maxRecordPayloadBytes").GetInt32(),
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var messageId = (await changed.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        await using var gateway = new GatewayHttpApp(host.Client.BaseAddress!.AbsoluteUri) { SigningKey = BusinessProcess.SigningKey };
        var routes = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        gateway.UseRoutes(routes.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var client = gateway.CreateClient();
        var path = $"/api/{source}/audit-deliveries";
        var requestId = Guid.NewGuid();
        var readPaths = new[] { path + "?state=DeadLettered", $"{path}/{messageId}", path + "/recovery-capacity",
            $"{path}/recoveries/{requestId}" };
        var retry = new Uri($"{path}/{messageId}/retry", UriKind.Relative);
        var request = new
        {
            requestId,
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "dependency-restored",
            actorId = "forged-actor",
            source = "identity",
            recoveredAt = DateTimeOffset.MaxValue,
        };
        await AssertReadsAsync(client, readPaths, HttpStatusCode.Unauthorized);
        using var anonymous = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        host.Authenticate(root: false);
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        await AssertReadsAsync(client, readPaths, HttpStatusCode.Forbidden);
        using var unprivileged = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.Forbidden, unprivileged.StatusCode);
        host.Authenticate();
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        await AssertReadsAsync(client, readPaths[..3], HttpStatusCode.OK);
        using var missing = await client.GetAsync(new Uri(readPaths[3], UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var beforeResponse = await host.Client.GetAsync(policyPath);
        var beforePools = await beforeResponse.Content.ReadApiDataAsync();
        using var accepted = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        Assert.Equal(source, receipt.GetProperty("source").GetString());
        Assert.Equal("test-operator", receipt.GetProperty("actorId").GetString());
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(messageId, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal("0", receipt.GetProperty("expectedRetryRevision").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        Assert.NotEqual(DateTimeOffset.MaxValue, receipt.GetProperty("recoveredAt").GetDateTimeOffset());
        using var replay = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var conflict = await client.PostAsJsonAsync(retry,
            new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "1", reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertReadsAsync(client, readPaths, HttpStatusCode.OK);
        using var found = await client.GetAsync(new Uri(readPaths[3], UriKind.Relative));
        Assert.Equal(receipt.GetRawText(), (await found.Content.ReadApiDataAsync()).GetRawText());
        using var state = await client.GetAsync(new Uri(readPaths[1], UriKind.Relative));
        var safe = await state.Content.ReadApiDataAsync();
        Assert.Equal("Pending", safe.GetProperty("state").GetString());
        Assert.Equal("1", safe.GetProperty("retryRevision").GetString());
        Assert.False(safe.TryGetProperty("payload", out _));
        Assert.False(safe.TryGetProperty("lastFailure", out _));
        using var afterResponse = await host.Client.GetAsync(policyPath);
        Assert.Equal(beforePools.GetRawText(), (await afterResponse.Content.ReadApiDataAsync()).GetRawText());
        var restored = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.EventName, restored.EventName);
        Assert.Equal(original.Payload, restored.Payload);
        Assert.Equal(original.OccurredAt, restored.OccurredAt);
        host.Authenticate(root: false);
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        await AssertReadsAsync(client, readPaths, HttpStatusCode.Forbidden);
        using var noLongerRoot = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.Forbidden, noLongerRoot.StatusCode);
    }

    private static async Task AssertReadsAsync(HttpClient client, IEnumerable<string> paths, HttpStatusCode expected)
    {
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(expected, response.StatusCode);
        }
    }
}
