using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SessionAuthorizationCoverageTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task EveryBusinessEndpointAndGatewayManagementOperation_RequiresCurrentRootSession()
    {
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        await using var costing = new BusinessHostApp<CostingHostMarker>("Costing", costs.ConnectionString);
        await using var pricing = new BusinessHostApp<PricingHostMarker>("Pricing", prices.ConnectionString);
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        Assert.True(await CheckAsync(costing, "/api/costing") > 10);
        Assert.True(await CheckAsync(pricing, "/api/pricing") > 10);
        Assert.Equal(4, await CheckAsync(gateway, "/gateway/routes", excludeDescription: true));
    }

    private static async Task<int> CheckAsync<T>(WebApplicationFactory<T> host, string prefix, bool excludeDescription = false) where T : class
    {
        using var client = host.CreateClient();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(prefix, StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(routes);
        var provider = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var checkedOperations = 0;
        foreach (var endpoint in routes)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            Assert.NotNull(methods);
            Assert.NotEmpty(methods);
            if (excludeDescription && endpoint.RoutePattern.RawText?.TrimEnd('/') == prefix && methods is ["GET"]) { continue; }
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var declarations = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.NotEmpty(declarations);
            var policy = await AuthorizationPolicy.CombineAsync(provider, declarations);
            Assert.NotNull(policy);
            Assert.Contains(policy.Requirements, requirement => requirement is CurrentRootSessionRequirement);
            checkedOperations += methods.Count;
        }
        Assert.True(checkedOperations > 0);
        return checkedOperations;
    }
}
