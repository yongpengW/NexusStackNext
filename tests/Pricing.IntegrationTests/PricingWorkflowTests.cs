using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingWorkflowTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task FailedAttempt_IsScheduledForRetryAndRemainsObservable()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch))).Value);
        var status = (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value;
        Assert.Equal("Retry", status.State);
        Assert.Equal("Failed", Assert.Single(status.History).Outcome);
        Assert.Null((await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
    }
    [PostgresFact]
    public async Task AcceptedCostChange_CanBeQueriedAfterReopeningTheApplication()
    {
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        await using (var app = CreateApplication())
        {
            await using var scope = app.CreateAsyncScope();
            var accepted = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
            Assert.True(accepted.IsSuccess, accepted.Error?.Message);
        }

        await using var reopened = CreateApplication();
        await using var readScope = reopened.CreateAsyncScope();
        var found = await readScope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(command.RequestId));
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Equal("Pending", found.Value.State);
        Assert.Equal(command.ItemId, found.Value.ItemId);
    }

    private ServiceProvider CreateApplication(PricingTaskOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString, options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task Worker_CompletesThePriceAndTaskTogether()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        PricingWorkLease? lease;
        do
        {
            lease = (await sender.SendAsync(new ClaimPricingWork())).Value;
            Assert.NotNull(lease);
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        } while (lease.TaskId != request.RequestId);
        var quote = (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
        Assert.Equal(100m, quote.BreakEvenPrice);
        Assert.Equal(quote.InputRevision, quote.CalculatedRevision);
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.State);
    }

    [PostgresFact]
    public async Task ConcurrentRequestRetries_ReturnOneTask_AndRejectDifferentContent()
    {
        await using var app = CreateApplication();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(request);
        }));
        Assert.All(accepted, result => Assert.Equal(request.RequestId, result.Value.TaskId));
        await using var check = app.CreateAsyncScope();
        var sender = check.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("pricing.request_conflict", (await sender.SendAsync(request with { Cost = 81m })).Error.Code);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task TwoWorkers_OnlyOneOwnsAnUnexpiredTask()
    {
        await using var app = CreateApplication();
        await using (var scope = app.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(
                new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m))).IsSuccess);
        }
        var leases = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ClaimPricingWork())).Value;
        }));
        Assert.Single(leases, x => x is not null);
    }

    [PostgresFact]
    public async Task ConcurrentInputChanges_WithTheSameVersion_OnlyAcceptOneRequest()
    {
        await using var app = CreateApplication();
        var itemId = Guid.NewGuid();
        var commands = new[]
        {
            new UpdatePricingCost(Guid.NewGuid(), itemId, 0, 80m, 0.2m),
            new UpdatePricingCost(Guid.NewGuid(), itemId, 0, 120m, 0.2m),
        };
        var results = await Task.WhenAll(commands.Select(async command =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
        }));
        var accepted = Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("pricing.version_conflict", Assert.Single(results, result => result.IsFailure).Error.Code);
        await using var check = app.CreateAsyncScope();
        var sender = check.ServiceProvider.GetRequiredService<ISender>();
        var winner = commands.Single(command => command.RequestId == accepted.Value.TaskId);
        var loser = commands.Single(command => command.RequestId != accepted.Value.TaskId);
        Assert.Equal(winner.Cost, (await sender.QueryAsync(new GetPriceQuote(itemId))).Value.Cost);
        Assert.Equal("pricing.not_found", (await sender.QueryAsync(new GetRecalculation(loser.RequestId))).Error.Code);
        Assert.NotNull((await sender.SendAsync(new ClaimPricingWork())).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
    }

    [PostgresFact]
    public async Task ExpiredExecution_RejectsWritesBeforeTakeover_AndExhaustsItsBudget()
    {
        await using var app = CreateApplication(new PricingTaskOptions { MaxAttempts = 1, LeaseDuration = TimeSpan.FromMilliseconds(500) });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Assert.False((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
        var task = (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value;
        Assert.Equal("Failed", task.State);
        Assert.Equal("pricing.attempts_exhausted", task.ErrorCode);
        Assert.Equal("Expired", Assert.Single(task.History).Outcome);
        Assert.Null((await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task ExpiredWorker_CannotCompleteOrFailTheNewOwnersTask()
    {
        await using var app = CreateApplication(new PricingTaskOptions { LeaseDuration = TimeSpan.FromMilliseconds(500) });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var old = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        await using var replacementApp = CreateApplication();
        await using var replacementScope = replacementApp.CreateAsyncScope();
        var replacementSender = replacementScope.ServiceProvider.GetRequiredService<ISender>();
        var replacement = (await replacementSender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(old.Epoch + 1, replacement.Epoch);
        Assert.False((await sender.SendAsync(new CompletePricingWork(old.TaskId, old.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailPricingWork(old.TaskId, old.Epoch))).Value);
        Assert.True((await replacementSender.SendAsync(new CompletePricingWork(replacement.TaskId, replacement.Epoch))).Value);
        var task = (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value;
        Assert.Equal(new[] { "Expired", "Succeeded" }, task.History.Select(x => x.Outcome));
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task NewInputWins_AndRecalculatingUnchangedInputDoesNotBumpVersion()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var original = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(original)).IsSuccess);
        var old = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 120m })).IsSuccess);
        var current = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(current.TaskId, current.Epoch))).Value);
        Assert.True((await sender.SendAsync(new CompletePricingWork(old.TaskId, old.Epoch))).Value);
        var quote = (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value;
        Assert.Equal(150m, quote.BreakEvenPrice);
        Assert.Equal(3, quote.Version);
        Assert.Equal("Superseded", (await sender.QueryAsync(new GetRecalculation(original.RequestId))).Value.State);
        Assert.Equal("pricing.cancel_conflict", (await sender.SendAsync(new CancelPricingWork(old.TaskId, old.Epoch))).Error.Code);
        Assert.Equal("pricing.renew_conflict", (await sender.SendAsync(new RenewPricingWork(old.TaskId, old.Epoch))).Error.Code);
        Assert.True((await sender.SendAsync(original with { RequestId = Guid.NewGuid(), ExpectedVersion = 3, Cost = 120m })).IsSuccess);
        var noChange = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(noChange.TaskId, noChange.Epoch))).Value);
        Assert.Equal(3, (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value.Version);
    }

    [PostgresFact]
    public async Task RetryBudgetIsBounded_AndManualRetryKeepsEarlierAttempts()
    {
        await using var app = CreateApplication(new PricingTaskOptions { MaxAttempts = 2, RetryDelay = TimeSpan.FromMilliseconds(50) });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var first = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new FailPricingWork(first.TaskId, first.Epoch))).Value);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        var second = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new FailPricingWork(second.TaskId, second.Epoch))).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
        Assert.Equal("Failed", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.State);
        Assert.True((await sender.SendAsync(new RetryPricingWork(request.RequestId, second.Epoch))).IsSuccess);
        Assert.Equal("pricing.retry_conflict", (await sender.SendAsync(new RetryPricingWork(request.RequestId, second.Epoch))).Error.Code);
        var third = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(3, third.Epoch);
        Assert.True((await sender.SendAsync(new CompletePricingWork(third.TaskId, third.Epoch))).Value);
        Assert.Equal(new[] { "Failed", "Failed", "Succeeded" },
            (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.History.Select(x => x.Outcome));
    }

    [PostgresFact]
    public async Task TaskInsertFailure_RollsBackTheCostChange()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var original = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(original)).IsSuccess);
        var changed = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Cost = 120m };
        await database.RejectTaskInsertAsync(changed.RequestId);
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(changed));
        var quote = (await sender.QueryAsync(new GetPriceQuote(original.ItemId))).Value;
        Assert.Equal(80m, quote.Cost);
        Assert.Equal(1, quote.Version);
        Assert.Equal("pricing.not_found", (await sender.QueryAsync(new GetRecalculation(changed.RequestId))).Error.Code);
    }

    [PostgresFact]
    public async Task CompletionFailure_RollsBackTheCalculatedPrice()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await database.RejectTaskCompletionAsync(lease.TaskId);
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch)));
        var quote = (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
        Assert.Null(quote.BreakEvenPrice);
        Assert.Equal(1, quote.Version);
        Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.State);
    }
}
