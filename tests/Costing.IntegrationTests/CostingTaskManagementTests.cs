using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingTaskManagementTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task DelayedRequest_RemainsUnclaimable_AndReplayKeepsItsFirstDeadline()
    {
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m, DelaySeconds: 120);
        DateTimeOffset firstDeadline;
        DateTimeOffset firstAcceptance;
        await using (var application = CreateApplication())
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var accepted = (await sender.SendAsync(request)).Value;
            firstDeadline = accepted.AvailableAt;
            Assert.NotNull(accepted.CreatedAt);
            firstAcceptance = accepted.CreatedAt.Value;
            Assert.Equal(firstAcceptance.AddSeconds(120), firstDeadline);
            Assert.Null((await sender.SendAsync(new ClaimCostingWork())).Value);
            Assert.Equal(80m, (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value.PurchaseCost);
        }

        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        var repeated = (await query.SendAsync(request)).Value;
        Assert.Equal(firstDeadline, repeated.AvailableAt);
        Assert.Equal(firstAcceptance, repeated.CreatedAt);
        Assert.Equal("Pending", repeated.State);
        Assert.Null((await query.SendAsync(new ClaimCostingWork())).Value);
        Assert.Equal("costing.request_conflict", (await query.SendAsync(request with { DelaySeconds = 121 })).Error.Code);
    }

    [PostgresFact]
    public async Task CancelRunningWork_PreservesAcceptedInputs_AndFencesLateResults()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        _ = (await sender.SendAsync(request)).Value;
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        var cancelled = (await sender.SendAsync(new CancelCostingWork(request.RequestId, lease.Epoch))).Value;
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(lease.Epoch, cancelled.Epoch);
        Assert.Equal("Cancelled", Assert.Single(cancelled.History).Outcome);
        Assert.NotNull(cancelled.History[0].FinishedAt);
        Assert.False((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Null((await sender.SendAsync(new ClaimCostingWork())).Value);
        Assert.Equal("costing.retry_conflict", (await sender.SendAsync(new RetryCostingWork(lease.TaskId, lease.Epoch))).Error.Code);
        Assert.Equal("costing.cancel_conflict", (await sender.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch + 1))).Error.Code);

        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        var repeated = (await query.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch))).Value;
        Assert.Equal(cancelled.History, repeated.History);
        Assert.Equal("Cancelled", (await query.QueryAsync(new GetCostCalculation(lease.TaskId))).Value.State);
        var sheet = (await query.QueryAsync(new GetCostSheet(request.ItemId))).Value;
        Assert.Equal(80m, sheet.PurchaseCost);
        Assert.Equal(20m, sheet.FreightCost);
        Assert.Null(sheet.UnitCost);
        Assert.Equal("costing.not_found", (await query.QueryAsync(new GetCostDelivery(lease.TaskId))).Error.Code);
    }

    [PostgresFact]
    public async Task RenewedLease_AllowsCompletionBeyondOriginalDeadline_AfterReopening()
    {
        var policy = new CostingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(3) };
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        CostingWorkLease original;
        CostingWorkLease renewed;
        await using (var application = CreateApplication(policy))
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            _ = (await sender.SendAsync(request)).Value;
            original = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            await WaitForDatabaseTimeAsync(original.ExpiresAt.AddSeconds(-1.5));
            renewed = (await sender.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch))).Value;
            Assert.Equal(original.Epoch, renewed.Epoch);
            Assert.True(renewed.ExpiresAt > original.ExpiresAt);
        }

        await WaitForDatabaseTimeAsync(original.ExpiresAt);
        await using var reopened = CreateApplication(policy);
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await query.SendAsync(new CompleteCostingWork(renewed.TaskId, renewed.Epoch))).Value);
        var completed = (await query.QueryAsync(new GetCostCalculation(renewed.TaskId))).Value;
        Assert.Equal("Succeeded", completed.State);
        Assert.Equal(1, completed.Attempts);
        Assert.Single(completed.History);
        Assert.Equal(100m, (await query.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
        Assert.Equal("Pending", (await query.QueryAsync(new GetCostDelivery(renewed.TaskId))).Value.State);
    }

    [PostgresFact]
    public async Task LeaseBudget_SurvivesConfigurationChanges_AndReturnsTheStoredDeadline()
    {
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        CostingWorkLease original;
        DateTimeOffset deadline;
        await using (var application = CreateApplication(new CostingTaskOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(3),
            MaxLeaseDuration = TimeSpan.FromSeconds(4),
        }))
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            _ = (await sender.SendAsync(request)).Value;
            original = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            var status = (await sender.QueryAsync(new GetCostCalculation(original.TaskId))).Value;
            Assert.Equal(original.ExpiresAt, status.LeaseUntil);
            Assert.NotNull(status.MaxLeaseUntil);
            deadline = status.MaxLeaseUntil.Value;
            Assert.Equal(TimeSpan.FromSeconds(4), deadline - Assert.Single(status.History).StartedAt);
        }

        await using var reopened = CreateApplication(new CostingTaskOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            MaxLeaseDuration = TimeSpan.FromHours(1),
        });
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("costing.renew_conflict", (await query.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch + 1))).Error.Code);
        var renewed = (await query.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch))).Value;
        Assert.Equal(deadline, renewed.ExpiresAt);
        Assert.Equal("costing.renew_conflict", (await query.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch))).Error.Code);
        var stored = (await query.QueryAsync(new GetCostCalculation(original.TaskId))).Value;
        Assert.Equal(renewed.ExpiresAt, stored.LeaseUntil);
        Assert.Equal(deadline, stored.MaxLeaseUntil);
        Assert.Equal(original.Epoch, stored.Epoch);
        Assert.Equal(1, stored.Attempts);
        Assert.Single(stored.History);
        _ = (await query.SendAsync(new CancelCostingWork(original.TaskId, original.Epoch))).Value;
        Assert.Equal("costing.renew_conflict", (await query.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch))).Error.Code);
    }

    [PostgresFact]
    public async Task RenewalBlockedPastOriginalDeadline_RollsBack_AndCannotReviveOldExecution()
    {
        await using var application = CreateApplication(new CostingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(4) });
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        _ = (await sender.SendAsync(request)).Value;
        var original = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "costing", original.TaskId))
        {
            await WaitForDatabaseTimeAsync(original.ExpiresAt.AddSeconds(-2));
            var renewal = sender.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch));
            try
            {
                await pause.WaitForWriterAsync();
                await WaitForDatabaseTimeAsync(original.ExpiresAt);
            }
            finally { await pause.ReleaseAsync(); }
            Assert.Equal("costing.renew_conflict", (await renewal).Error.Code);
        }

        await using var check = application.CreateAsyncScope();
        var query = check.ServiceProvider.GetRequiredService<ISender>();
        var unchanged = (await query.QueryAsync(new GetCostCalculation(original.TaskId))).Value;
        Assert.Equal(original.ExpiresAt, unchanged.LeaseUntil);
        Assert.Equal("Running", unchanged.State);
        Assert.Equal("Running", Assert.Single(unchanged.History).Outcome);
        Assert.False((await query.SendAsync(new CompleteCostingWork(original.TaskId, original.Epoch))).Value);
        Assert.False((await query.SendAsync(new FailCostingWork(original.TaskId, original.Epoch))).Value);
        Assert.Null((await query.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
        Assert.Equal("costing.not_found", (await query.QueryAsync(new GetCostDelivery(original.TaskId))).Error.Code);
        var successor = (await query.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True(successor.Epoch > original.Epoch);
        Assert.Equal("costing.renew_conflict", (await query.SendAsync(new RenewCostingWork(original.TaskId, original.Epoch))).Error.Code);
        Assert.True((await query.SendAsync(new CompleteCostingWork(successor.TaskId, successor.Epoch))).Value);
        var recovered = (await query.QueryAsync(new GetCostCalculation(original.TaskId))).Value;
        Assert.Equal(new[] { "Expired", "Succeeded" }, recovered.History.Select(attempt => attempt.Outcome));
    }

    [PostgresFact]
    public async Task TaskList_PagesMetadata_AndFiltersByStateAndBusinessObject()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var first = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m, DelaySeconds: 120);
        var second = first with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        var third = first with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        foreach (var request in new[] { first, second, third }) { _ = (await sender.SendAsync(request)).Value; }
        _ = (await sender.SendAsync(new CancelCostingWork(second.RequestId, 0))).Value;
        var firstPage = (await sender.QueryAsync(new ListCostCalculations(Limit: 2))).Value;
        var secondPage = (await sender.QueryAsync(new ListCostCalculations(Page: 2, Limit: 2))).Value;
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(3, secondPage.Total);
        Assert.Equal(new[] { third.RequestId, second.RequestId }, firstPage.Items.Select(item => item.TaskId));
        Assert.Equal(first.RequestId, Assert.Single(secondPage.Items).TaskId);
        Assert.All(firstPage.Items, item => Assert.NotNull(item.CreatedAt));
        Assert.All(firstPage.Items, item => Assert.Equal(0, item.Epoch));
        Assert.All(firstPage.Items, item => Assert.Null(item.LeaseUntil));
        var filtered = (await sender.QueryAsync(new ListCostCalculations(State: "Pending", ItemId: first.ItemId))).Value;
        Assert.Equal(1, filtered.Total);
        Assert.Equal(first.RequestId, Assert.Single(filtered.Items).TaskId);
        var cancelled = (await sender.QueryAsync(new ListCostCalculations(State: "Cancelled"))).Value;
        Assert.Equal(second.RequestId, Assert.Single(cancelled.Items).TaskId);
        Assert.Empty((await sender.QueryAsync(new ListCostCalculations(State: "Running"))).Value.Items);
        foreach (var invalid in new[]
        {
            new ListCostCalculations(Page: 0), new ListCostCalculations(Limit: 201),
            new ListCostCalculations(Page: int.MaxValue), new ListCostCalculations(State: "unknown"),
            new ListCostCalculations(ItemId: Guid.Empty),
        })
        {
            Assert.Equal("costing.invalid_query", (await sender.QueryAsync(invalid)).Error.Code);
        }
    }

    [PostgresFact]
    public async Task CancellationRacingCompletion_HasOnlyTheOutcomeOfTheLockWinner()
    {
        foreach (var cancelFirst in new[] { true, false })
        {
            await database.ResetAsync();
            await using var application = CreateApplication();
            await using var firstScope = application.CreateAsyncScope();
            await using var secondScope = application.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<ISender>();
            var second = secondScope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            _ = (await first.SendAsync(request)).Value;
            var lease = (await first.SendAsync(new ClaimCostingWork())).Value!;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "costing", lease.TaskId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch)) : null;
                var completion = cancelFirst ? null : first.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch));
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch));
                    completion ??= second.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch));
                    await pause.WaitForWaitersAsync(2);
                }
                finally { await pause.ReleaseAsync(); }
                Assert.Equal(cancelFirst, (await cancellation!).IsSuccess);
                Assert.Equal(!cancelFirst, (await completion!).Value);
                if (!cancelFirst) { Assert.Equal("costing.cancel_conflict", (await cancellation!).Error.Code); }
            }
            var status = (await first.QueryAsync(new GetCostCalculation(lease.TaskId))).Value;
            Assert.Equal(cancelFirst ? "Cancelled" : "Succeeded", status.State);
            Assert.Equal(status.State, Assert.Single(status.History).Outcome);
            var sheet = (await first.QueryAsync(new GetCostSheet(request.ItemId))).Value;
            Assert.Equal(cancelFirst ? null : (decimal?)100m, sheet.UnitCost);
            var delivery = await first.QueryAsync(new GetCostDelivery(lease.TaskId));
            Assert.Equal(!cancelFirst, delivery.IsSuccess);
            if (!cancelFirst) { Assert.Equal("Pending", delivery.Value.State); }
        }
    }

    [PostgresFact]
    public async Task CancellationRacingClaim_NeverReopensCancelledWork_OrCancelsAnUnseenEpoch()
    {
        foreach (var cancelFirst in new[] { true, false })
        {
            await database.ResetAsync();
            await using var application = CreateApplication();
            await using var firstScope = application.CreateAsyncScope();
            await using var secondScope = application.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<ISender>();
            var second = secondScope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            _ = (await first.SendAsync(request)).Value;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "costing", request.RequestId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelCostingWork(request.RequestId, 0)) : null;
                var claim = cancelFirst ? null : first.SendAsync(new ClaimCostingWork());
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelCostingWork(request.RequestId, 0));
                    claim ??= second.SendAsync(new ClaimCostingWork());
                    if (cancelFirst) { Assert.Null((await claim).Value); }
                    else { await pause.WaitForWaitersAsync(2); }
                }
                finally { await pause.ReleaseAsync(); }
                Assert.Equal(cancelFirst, (await cancellation!).IsSuccess);
                if (!cancelFirst)
                {
                    Assert.Equal("costing.cancel_conflict", (await cancellation!).Error.Code);
                    Assert.NotNull((await claim!).Value);
                }
            }
            var status = (await first.QueryAsync(new GetCostCalculation(request.RequestId))).Value;
            Assert.Equal(cancelFirst ? "Cancelled" : "Running", status.State);
            Assert.Equal(cancelFirst ? 0 : 1, status.Epoch);
            Assert.Equal(cancelFirst ? 0 : 1, status.History.Count);
            Assert.Null((await second.SendAsync(new ClaimCostingWork())).Value);
        }
    }

    [PostgresFact]
    public async Task CancellationRacingFailure_PreservesTheAttempt_AndAllowsOrderedRetryThenCancel()
    {
        foreach (var cancelFirst in new[] { true, false })
        {
            await database.ResetAsync();
            await using var application = CreateApplication();
            await using var firstScope = application.CreateAsyncScope();
            await using var secondScope = application.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<ISender>();
            var second = secondScope.ServiceProvider.GetRequiredService<ISender>();
            var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
            _ = (await first.SendAsync(request)).Value;
            var lease = (await first.SendAsync(new ClaimCostingWork())).Value!;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "costing", lease.TaskId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch)) : null;
                var failure = cancelFirst ? null : first.SendAsync(new FailCostingWork(lease.TaskId, lease.Epoch));
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch));
                    failure ??= second.SendAsync(new FailCostingWork(lease.TaskId, lease.Epoch));
                    await pause.WaitForWaitersAsync(2);
                }
                finally { await pause.ReleaseAsync(); }
                Assert.True((await cancellation!).IsSuccess);
                Assert.Equal(!cancelFirst, (await failure!).Value);
            }
            var status = (await first.QueryAsync(new GetCostCalculation(lease.TaskId))).Value;
            Assert.Equal("Cancelled", status.State);
            Assert.Equal(cancelFirst ? "Cancelled" : "Failed", Assert.Single(status.History).Outcome);
            Assert.Equal(lease.Epoch, status.Epoch);
            Assert.Null((await second.SendAsync(new ClaimCostingWork())).Value);
            Assert.Null((await first.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
        }
    }

    [PostgresFact]
    public async Task FailedWork_CannotBeCancelledUntilRetryCommits_AndNewIntentNeedsANewIdentity()
    {
        await using var application = CreateApplication(new CostingTaskOptions { MaxAttempts = 1 });
        await using var firstScope = application.CreateAsyncScope();
        await using var secondScope = application.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ISender>();
        var second = secondScope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        var accepted = (await first.SendAsync(request)).Value;
        var lease = (await first.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True((await first.SendAsync(new FailCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("costing.cancel_conflict", (await second.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch))).Error.Code);
        await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "costing", lease.TaskId))
        {
            var retry = first.SendAsync(new RetryCostingWork(lease.TaskId, lease.Epoch));
            Task<NexusStackNext.BuildingBlocks.Domain.Result<CostCalculationStatus>>? cancel = null;
            try
            {
                await pause.WaitForWriterAsync();
                cancel = second.SendAsync(new CancelCostingWork(lease.TaskId, lease.Epoch));
                await pause.WaitForWaitersAsync(2);
            }
            finally { await pause.ReleaseAsync(); }
            Assert.True((await retry).IsSuccess);
            Assert.True((await cancel!).IsSuccess);
        }
        var cancelled = (await first.QueryAsync(new GetCostCalculation(lease.TaskId))).Value;
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(accepted.CreatedAt, cancelled.CreatedAt);
        Assert.Equal(0, cancelled.Attempts);
        Assert.Equal("Failed", Assert.Single(cancelled.History).Outcome);
        Assert.Equal("costing.retry_conflict", (await first.SendAsync(new RetryCostingWork(lease.TaskId, lease.Epoch))).Error.Code);
        Assert.Equal("Cancelled", (await first.SendAsync(request)).Value.State);
        var sheet = (await first.QueryAsync(new GetCostSheet(request.ItemId))).Value;
        var next = (await first.SendAsync(request with { RequestId = Guid.NewGuid(), ExpectedVersion = sheet.Version })).Value;
        var freshLease = (await second.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(next.TaskId, freshLease.TaskId);
        Assert.True((await second.SendAsync(new CompleteCostingWork(freshLease.TaskId, freshLease.Epoch))).Value);
        Assert.Equal("Cancelled", (await first.QueryAsync(new GetCostCalculation(lease.TaskId))).Value.State);
    }

    [PostgresFact]
    public async Task ManualDelay_RejectsInvalidBoundsWithoutInputs_AndAcceptsThirtyDays()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        foreach (var delay in new[] { -1, 2_592_001 })
        {
            Assert.Equal("costing.invalid_input", (await sender.SendAsync(request with { DelaySeconds = delay })).Error.Code);
            Assert.Equal("costing.not_found", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Error.Code);
            Assert.Equal("costing.not_found", (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Error.Code);
        }
        var accepted = (await sender.SendAsync(request with { DelaySeconds = 2_592_000 })).Value;
        Assert.NotNull(accepted.CreatedAt);
        Assert.Equal(TimeSpan.FromDays(30), accepted.AvailableAt - accepted.CreatedAt.Value);
        Assert.Null((await sender.SendAsync(new ClaimCostingWork())).Value);
    }

    private async Task WaitForDatabaseTimeAsync(DateTimeOffset instant)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp() >= @instant", connection);
            command.Parameters.AddWithValue("instant", instant);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private ServiceProvider CreateApplication(CostingTaskOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString, options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
