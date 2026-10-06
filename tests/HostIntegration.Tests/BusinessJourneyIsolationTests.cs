using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class BusinessJourneyIsolationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task CostingJourneys_DoNotShareCostInputsOrTaskCancellation()
    {
        await using var first = await databases.CreateAsync("costing");
        await using var second = await databases.CreateAsync("costing");
        await using var firstHost = TaskOperationTests.CreateCostingApp(first.ConnectionString, "first-operator");
        await using var secondHost = TaskOperationTests.CreateCostingApp(second.ConnectionString, "second-operator");
        await using var firstScope = firstHost.Services.CreateAsyncScope();
        await using var secondScope = secondHost.Services.CreateAsyncScope();
        var firstSender = firstScope.ServiceProvider.GetRequiredService<ISender>();
        var secondSender = secondScope.ServiceProvider.GetRequiredService<ISender>();
        var itemId = Guid.Parse("00000000-0000-0000-0000-000000000114");
        var requestId = Guid.Parse("10000000-0000-0000-0000-000000000114");
        Assert.True((await firstSender.SendAsync(new UpdateCostInputs(requestId, itemId, 0, 80m, 20m))).IsSuccess);
        Assert.True((await secondSender.QueryAsync(new GetCostSheet(itemId))).IsFailure);
        Assert.True((await secondSender.SendAsync(new UpdateCostInputs(requestId, itemId, 0, 90m, 10m))).IsSuccess);
        Assert.Equal(80m, (await firstSender.QueryAsync(new GetCostSheet(itemId))).Value.PurchaseCost);
        Assert.Equal(90m, (await secondSender.QueryAsync(new GetCostSheet(itemId))).Value.PurchaseCost);
        Assert.True((await firstSender.SendAsync(new CancelCostingWork(requestId, 0))).IsSuccess);
        Assert.Equal("Cancelled", (await firstSender.QueryAsync(new GetCostCalculation(requestId))).Value.State);
        Assert.Equal("Pending", (await secondSender.QueryAsync(new GetCostCalculation(requestId))).Value.State);
    }

    [PostgresFact]
    public async Task PricingJourneys_DoNotShareQuotesOrTaskCancellation()
    {
        await using var first = await databases.CreateAsync("pricing");
        await using var second = await databases.CreateAsync("pricing");
        await using var firstHost = TaskOperationTests.CreatePricingApp(first.ConnectionString, "first-operator");
        await using var secondHost = TaskOperationTests.CreatePricingApp(second.ConnectionString, "second-operator");
        await using var firstScope = firstHost.Services.CreateAsyncScope();
        await using var secondScope = secondHost.Services.CreateAsyncScope();
        var firstSender = firstScope.ServiceProvider.GetRequiredService<ISender>();
        var secondSender = secondScope.ServiceProvider.GetRequiredService<ISender>();
        var itemId = Guid.Parse("00000000-0000-0000-0000-000000000114");
        var requestId = Guid.Parse("10000000-0000-0000-0000-000000000114");
        Assert.True((await firstSender.SendAsync(new UpdatePricingCost(requestId, itemId, 0, 80m, 0.2m))).IsSuccess);
        Assert.True((await secondSender.QueryAsync(new GetPriceQuote(itemId))).IsFailure);
        Assert.True((await secondSender.SendAsync(new UpdatePricingCost(requestId, itemId, 0, 90m, 0.1m))).IsSuccess);
        Assert.Equal(80m, (await firstSender.QueryAsync(new GetPriceQuote(itemId))).Value.Cost);
        Assert.Equal(90m, (await secondSender.QueryAsync(new GetPriceQuote(itemId))).Value.Cost);
        Assert.True((await firstSender.SendAsync(new CancelPricingWork(requestId, 0))).IsSuccess);
        Assert.Equal("Cancelled", (await firstSender.QueryAsync(new GetRecalculation(requestId))).Value.State);
        Assert.Equal("Pending", (await secondSender.QueryAsync(new GetRecalculation(requestId))).Value.State);
    }
}
