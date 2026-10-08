using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationEndpointInventoryTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task EveryRealHostEndpoint_IsObservedOrExplicitlyExcluded()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await using (var operation = await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            await PricingDatabase.MigrateAsync(database.ConnectionString);
        }
        await using var platform = new PlatformApp { SchedulingWorkerEnabled = false };
        await InspectAsync(platform, "platform");
        await using var costing = new BusinessHostApp<CostingHostMarker>("Costing", database.ConnectionString);
        var costingRecords = await InspectAsync(costing, "costing");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.cost.update");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.task.retry"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "CostCalculation");
        Assert.Contains(costingRecords, record => record.Metadata?.Action == "costing.task.cancel"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "CostCalculation");
        await using (var scope = costing.Services.CreateAsyncScope())
        {
            var taskId = Guid.NewGuid();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new CancelCostingWork(taskId, 0))).IsFailure);
            Assert.True((await sender.SendAsync(new RenewCostingWork(taskId, 0))).IsFailure);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var records = (await ReadAsync(journal)).Where(record => record.Metadata?.TaskId == taskId && record.Phase == "finished").ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, record => Assert.Equal("rejected", record.Outcome));
        }
        await using var pricing = new BusinessHostApp<PricingHostMarker>("Pricing", database.ConnectionString);
        var pricingRecords = await InspectAsync(pricing, "pricing");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.fee.update");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.task.retry"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "Recalculation");
        Assert.Contains(pricingRecords, record => record.Metadata?.Action == "pricing.task.cancel"
            && record.Metadata.SubjectId == "98e26a25-036d-49cb-aaef-2e6197a33ce0" && record.Metadata.SubjectType == "Recalculation");
        await using (var scope = pricing.Services.CreateAsyncScope())
        {
            var taskId = Guid.NewGuid();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new CancelPricingWork(taskId, 0))).IsFailure);
            Assert.True((await sender.SendAsync(new RenewPricingWork(taskId, 0))).IsFailure);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var records = (await ReadAsync(journal)).Where(record => record.Metadata?.TaskId == taskId && record.Phase == "finished").ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, record => Assert.Equal("rejected", record.Outcome));
        }
        await using var backend = await GatewayBackend.StartAsync("inventory");
        await using var gateway = new GatewayHttpApp(backend.Address);
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend" }));
        var gatewayRecords = await InspectAsync(gateway, "gateway");
        Assert.Contains(gatewayRecords, record => record.Metadata?.ExecutionRole == "proxy");
    }

    private async Task<OperationObservedV1[]> InspectAsync<T>(WebApplicationFactory<T> host, string source) where T : class
    {
        using var client = host.CreateClient();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(endpoints);
        await using var scope = host.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observed = new List<OperationObservedV1>();
        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText;
            Assert.False(string.IsNullOrWhiteSpace(route));
            if (route.StartsWith("/hubs", StringComparison.Ordinal)) { continue; } // 实时协议不属于本轮 HTTP 业务入口。
            var frameworkExcluded = route.StartsWith("/health", StringComparison.Ordinal) || route.StartsWith("/openapi", StringComparison.Ordinal)
                || route.StartsWith("/gateway/openapi", StringComparison.Ordinal);
            var suppression = endpoint.Metadata.GetMetadata<OperationLogSuppression>();
            if (route is "/api/auditing/entries" or "/api/auditing/operations") { Assert.NotNull(suppression); }
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var correlation = "inventory-" + Guid.NewGuid().ToString("N");
                using var request = new HttpRequestMessage(new HttpMethod(method), Expand(endpoint.RoutePattern));
                request.Headers.Add("X-Correlation-Id", correlation);
                using var response = await client.SendAsync(request);
                if (suppression is not null || frameworkExcluded)
                {
                    if (suppression is not null) { Assert.False(string.IsNullOrWhiteSpace(suppression.Reason)); }
                    Assert.DoesNotContain(await ReadAsync(journal), item => item.Metadata?.CorrelationId == correlation);
                    continue;
                }
                var finished = await WaitAsync(journal, correlation);
                Assert.Equal(source, finished.Source);
                Assert.Equal(route, finished.RouteTemplate);
                Assert.Equal((int)response.StatusCode, finished.StatusCode);
                Assert.Null(finished.ActorId);
                observed.Add(finished);
            }
        }
        Assert.NotEmpty(observed);
        foreach (var path in new[] { "/swagger/index.html", "/swagger/swagger-ui.css" })
        {
            var correlation = "static-" + Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Correlation-Id", correlation);
            using var response = await client.SendAsync(request);
            Assert.DoesNotContain(await ReadAsync(journal), item => item.Metadata?.CorrelationId == correlation);
        }
        output.WriteLine($"{source}: {endpoints.Length} real route endpoints; {observed.Count} business HTTP operations verified.");
        return [.. observed];
    }

    internal static async Task<OperationObservedV1[]> ReadAsync(IOutboxStore journal) =>
        (await journal.ReadPendingAsync(1000, DateTimeOffset.UtcNow)).Select(entry =>
            new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(entry.Payload)).ToArray();

    internal static async Task<OperationObservedV1> WaitAsync(IOutboxStore journal, string correlation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var found = (await ReadAsync(journal)).Where(item => item.Metadata?.CorrelationId == correlation && item.Phase == "finished").ToArray();
            if (found.Length > 0) { return Assert.Single(found); }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static Uri Expand(RoutePattern pattern) => new("/" + string.Join('/', pattern.PathSegments.Select(segment =>
        string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter when parameter.ParameterPolicies.Any(policy => policy.Content == "guid") => "98e26a25-036d-49cb-aaef-2e6197a33ce0",
            RoutePatternParameterPart parameter when parameter.ParameterPolicies.Any(policy => policy.Content is "int" or "long") => "1",
            _ => "inventory",
        })))), UriKind.Relative);

}
