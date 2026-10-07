using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class BusinessFactCapacityPolicyGatewayTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task PricingPostgres_PricingRoutes_RequiresRootAndTrustedActor_WithExactInt64()
        => VerifyAsync("pricing", "routes.pricing.json");

    [PostgresFact]
    public Task CostingPostgres_BusinessRoutes_RequiresRootAndTrustedActor_WithExactInt64()
        => VerifyAsync("costing", "routes.business.json");

    [PostgresFact]
    public Task PricingPostgres_BusinessRoutes_RequiresRootAndTrustedActor_WithExactInt64()
        => VerifyAsync("pricing", "routes.business.json");

    private async Task VerifyAsync(string source, string configurationFile)
    {
        await using var database = await databases.CreateAsync(source);
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", database.ConnectionString);
        await using var gateway = new GatewayHttpApp(host.Client.BaseAddress!.AbsoluteUri) { SigningKey = BusinessProcess.SigningKey };
        var routes = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        gateway.UseRoutes(routes.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var client = gateway.CreateClient();
        var path = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        var requestId = Guid.NewGuid();
        var request = new
        {
            requestId,
            expectedPolicyRevision = "1",
            maxRecords = "9007199254740993",
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
            actorId = "forged-actor",
            owner = "identity",
            eventName = "forged-event"
        };
        using var anonymous = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        host.Authenticate(root: false);
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        using var unprivileged = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Forbidden, unprivileged.StatusCode);
        host.Authenticate();
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        using var beforeResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        var before = await beforeResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, before.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal(0, before.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        using var accepted = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
        using var afterResponse = await client.GetAsync(path);
        var after = await afterResponse.Content.ReadApiDataAsync();
        Assert.Equal("9007199254740993", after.GetProperty("maxRecords").GetString());
        Assert.Equal(0, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        using var replay = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var conflict = await client.PutAsJsonAsync(path,
            new FactCapacityPolicyRequest(requestId, 2, 9007199254740994, 268435456, 16384, "operator-adjustment"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var reader = source == "costing"
            ? TaskOperationTests.CreateCostingApp(database.ConnectionString, null)
            : TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await using var scope = reader.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var fact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal($"{source}.fact-capacity-policy-changed.v1", fact.EventName);
        using var payload = JsonDocument.Parse(fact.Payload);
        Assert.Equal("test-operator", payload.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(9007199254740993, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        Assert.NotEqual(requestId, fact.Id);
        host.Authenticate(root: false);
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        using var forbiddenReplay = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenReplay.StatusCode);
    }
}
