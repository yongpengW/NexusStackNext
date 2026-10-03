using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingTaskManagementTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task DelayedCostRequest_RemainsUnclaimable_AndReplayKeepsItsFirstDeadline()
    {
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m, DelaySeconds: 120);
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
            Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
            Assert.Equal(80m, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.Cost);
        }

        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        var repeated = (await query.SendAsync(request)).Value;
        Assert.Equal(firstDeadline, repeated.AvailableAt);
        Assert.Equal(firstAcceptance, repeated.CreatedAt);
        Assert.Equal("Pending", repeated.State);
        Assert.Null((await query.SendAsync(new ClaimPricingWork())).Value);
        Assert.Equal("pricing.request_conflict", (await query.SendAsync(request with { DelaySeconds = 121 })).Error.Code);
    }

    [PostgresFact]
    public async Task DelayedFeeRequest_PreservesCost_AndReplayKeepsItsFirstDeadline()
    {
        var itemId = Guid.NewGuid();
        UpdatePricingFee request;
        DateTimeOffset firstDeadline;
        DateTimeOffset firstAcceptance;
        await using (var application = CreateApplication())
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            _ = (await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), itemId, 0, 80m, 0.2m))).Value;
            var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
            var quote = (await sender.QueryAsync(new GetPriceQuote(itemId))).Value;
            request = new UpdatePricingFee(Guid.NewGuid(), itemId, quote.Version, 0.3m, DelaySeconds: 120);
            var accepted = (await sender.SendAsync(request)).Value;
            firstDeadline = accepted.AvailableAt;
            Assert.NotNull(accepted.CreatedAt);
            firstAcceptance = accepted.CreatedAt.Value;
            Assert.Equal(firstAcceptance.AddSeconds(120), firstDeadline);
            Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
            Assert.Equal(80m, (await sender.QueryAsync(new GetPriceQuote(itemId))).Value.Cost);
        }

        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        var repeated = (await query.SendAsync(request)).Value;
        Assert.Equal(firstDeadline, repeated.AvailableAt);
        Assert.Equal("Pending", repeated.State);
        Assert.Equal(firstAcceptance, repeated.CreatedAt);
        Assert.Null((await query.SendAsync(new ClaimPricingWork())).Value);
        Assert.Equal("pricing.request_conflict", (await query.SendAsync(request with { DelaySeconds = 121 })).Error.Code);
    }

    [PostgresFact]
    public async Task CancelRunningWork_PreservesAcceptedInputs_AndFencesLateResults()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        _ = (await sender.SendAsync(request)).Value;
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        var cancelled = (await sender.SendAsync(new CancelPricingWork(request.RequestId, lease.Epoch))).Value;
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(lease.Epoch, cancelled.Epoch);
        Assert.Equal("Cancelled", Assert.Single(cancelled.History).Outcome);
        Assert.NotNull(cancelled.History[0].FinishedAt);
        Assert.False((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
        Assert.Equal("pricing.retry_conflict", (await sender.SendAsync(new RetryPricingWork(lease.TaskId, lease.Epoch))).Error.Code);
        Assert.Equal("pricing.cancel_conflict", (await sender.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch + 1))).Error.Code);

        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        var repeated = (await query.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch))).Value;
        Assert.Equal(cancelled.History, repeated.History);
        Assert.Equal("Cancelled", (await query.QueryAsync(new GetRecalculation(lease.TaskId))).Value.State);
        var quote = (await query.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
        Assert.Equal(80m, quote.Cost);
        Assert.Equal(0.2m, quote.FeeRate);
        Assert.Null(quote.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task RenewedLease_AllowsCompletionBeyondOriginalDeadline_AfterReopening()
    {
        var policy = new PricingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(3) };
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        PricingWorkLease original;
        PricingWorkLease renewed;
        await using (var application = CreateApplication(policy))
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            _ = (await sender.SendAsync(request)).Value;
            original = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            await WaitForDatabaseTimeAsync(original.ExpiresAt.AddSeconds(-1.5));
            renewed = (await sender.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch))).Value;
            Assert.Equal(original.Epoch, renewed.Epoch);
            Assert.True(renewed.ExpiresAt > original.ExpiresAt);
        }

        await WaitForDatabaseTimeAsync(original.ExpiresAt);
        await using var reopened = CreateApplication(policy);
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await query.SendAsync(new CompletePricingWork(renewed.TaskId, renewed.Epoch))).Value);
        var completed = (await query.QueryAsync(new GetRecalculation(renewed.TaskId))).Value;
        Assert.Equal("Succeeded", completed.State);
        Assert.Equal(1, completed.Attempts);
        Assert.Single(completed.History);
        Assert.Equal(100m, (await query.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task LeaseBudget_SurvivesConfigurationChanges_AndReturnsTheStoredDeadline()
    {
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        PricingWorkLease original;
        DateTimeOffset deadline;
        await using (var application = CreateApplication(new PricingTaskOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(3),
            MaxLeaseDuration = TimeSpan.FromSeconds(4),
        }))
        {
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            _ = (await sender.SendAsync(request)).Value;
            original = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            var status = (await sender.QueryAsync(new GetRecalculation(original.TaskId))).Value;
            Assert.Equal(original.ExpiresAt, status.LeaseUntil);
            Assert.NotNull(status.MaxLeaseUntil);
            deadline = status.MaxLeaseUntil.Value;
            Assert.Equal(TimeSpan.FromSeconds(4), deadline - Assert.Single(status.History).StartedAt);
        }

        await using var reopened = CreateApplication(new PricingTaskOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            MaxLeaseDuration = TimeSpan.FromHours(1),
        });
        await using var reopenedScope = reopened.CreateAsyncScope();
        var query = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("pricing.renew_conflict", (await query.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch + 1))).Error.Code);
        var renewed = (await query.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch))).Value;
        Assert.Equal(deadline, renewed.ExpiresAt);
        Assert.Equal("pricing.renew_conflict", (await query.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch))).Error.Code);
        var stored = (await query.QueryAsync(new GetRecalculation(original.TaskId))).Value;
        Assert.Equal(renewed.ExpiresAt, stored.LeaseUntil);
        Assert.Equal(deadline, stored.MaxLeaseUntil);
        Assert.Equal(original.Epoch, stored.Epoch);
        Assert.Equal(1, stored.Attempts);
        Assert.Single(stored.History);
        _ = (await query.SendAsync(new CancelPricingWork(original.TaskId, original.Epoch))).Value;
        Assert.Equal("pricing.renew_conflict", (await query.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch))).Error.Code);
    }

    [PostgresFact]
    public async Task RenewalBlockedPastOriginalDeadline_RollsBack_AndCannotReviveOldExecution()
    {
        await using var application = CreateApplication(new PricingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(4) });
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        _ = (await sender.SendAsync(request)).Value;
        var original = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "pricing", original.TaskId))
        {
            await WaitForDatabaseTimeAsync(original.ExpiresAt.AddSeconds(-2));
            var renewal = sender.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch));
            try
            {
                await pause.WaitForWriterAsync();
                await WaitForDatabaseTimeAsync(original.ExpiresAt);
            }
            finally { await pause.ReleaseAsync(); }
            Assert.Equal("pricing.renew_conflict", (await renewal).Error.Code);
        }

        await using var check = application.CreateAsyncScope();
        var query = check.ServiceProvider.GetRequiredService<ISender>();
        var unchanged = (await query.QueryAsync(new GetRecalculation(original.TaskId))).Value;
        Assert.Equal(original.ExpiresAt, unchanged.LeaseUntil);
        Assert.Equal("Running", unchanged.State);
        Assert.Equal("Running", Assert.Single(unchanged.History).Outcome);
        Assert.False((await query.SendAsync(new CompletePricingWork(original.TaskId, original.Epoch))).Value);
        Assert.False((await query.SendAsync(new FailPricingWork(original.TaskId, original.Epoch))).Value);
        Assert.Null((await query.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
        var successor = (await query.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True(successor.Epoch > original.Epoch);
        Assert.Equal("pricing.renew_conflict", (await query.SendAsync(new RenewPricingWork(original.TaskId, original.Epoch))).Error.Code);
        Assert.True((await query.SendAsync(new CompletePricingWork(successor.TaskId, successor.Epoch))).Value);
        var recovered = (await query.QueryAsync(new GetRecalculation(original.TaskId))).Value;
        Assert.Equal(new[] { "Expired", "Succeeded" }, recovered.History.Select(attempt => attempt.Outcome));
    }

    [PostgresFact]
    public async Task TaskList_PagesMetadata_AndFiltersByStateAndBusinessObject()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var first = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m, DelaySeconds: 120);
        var second = first with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        var third = first with { RequestId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        foreach (var request in new[] { first, second, third }) { _ = (await sender.SendAsync(request)).Value; }
        _ = (await sender.SendAsync(new CancelPricingWork(second.RequestId, 0))).Value;
        var firstPage = (await sender.QueryAsync(new ListRecalculations(Limit: 2))).Value;
        var secondPage = (await sender.QueryAsync(new ListRecalculations(Page: 2, Limit: 2))).Value;
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(3, secondPage.Total);
        Assert.Equal(new[] { third.RequestId, second.RequestId }, firstPage.Items.Select(item => item.TaskId));
        Assert.Equal(first.RequestId, Assert.Single(secondPage.Items).TaskId);
        Assert.All(firstPage.Items, item => Assert.NotNull(item.CreatedAt));
        Assert.All(firstPage.Items, item => Assert.Equal(0, item.Epoch));
        Assert.All(firstPage.Items, item => Assert.Null(item.LeaseUntil));
        var filtered = (await sender.QueryAsync(new ListRecalculations(State: "Pending", ItemId: first.ItemId))).Value;
        Assert.Equal(1, filtered.Total);
        Assert.Equal(first.RequestId, Assert.Single(filtered.Items).TaskId);
        var cancelled = (await sender.QueryAsync(new ListRecalculations(State: "Cancelled"))).Value;
        Assert.Equal(second.RequestId, Assert.Single(cancelled.Items).TaskId);
        Assert.Empty((await sender.QueryAsync(new ListRecalculations(State: "Running"))).Value.Items);
        foreach (var invalid in new[]
        {
            new ListRecalculations(Page: 0), new ListRecalculations(Limit: 201),
            new ListRecalculations(Page: int.MaxValue), new ListRecalculations(State: "unknown"),
            new ListRecalculations(ItemId: Guid.Empty),
        })
        {
            Assert.Equal("pricing.invalid_query", (await sender.QueryAsync(invalid)).Error.Code);
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
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            _ = (await first.SendAsync(request)).Value;
            var lease = (await first.SendAsync(new ClaimPricingWork())).Value!;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "pricing", lease.TaskId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch)) : null;
                var completion = cancelFirst ? null : first.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch));
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch));
                    completion ??= second.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch));
                    await pause.WaitForWaitersAsync(2);
                }
                finally { await pause.ReleaseAsync(); }
                Assert.Equal(cancelFirst, (await cancellation!).IsSuccess);
                Assert.Equal(!cancelFirst, (await completion!).Value);
                if (!cancelFirst) { Assert.Equal("pricing.cancel_conflict", (await cancellation!).Error.Code); }
            }
            var status = (await first.QueryAsync(new GetRecalculation(lease.TaskId))).Value;
            Assert.Equal(cancelFirst ? "Cancelled" : "Succeeded", status.State);
            Assert.Equal(status.State, Assert.Single(status.History).Outcome);
            var quote = (await first.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
            Assert.Equal(cancelFirst ? null : (decimal?)100m, quote.BreakEvenPrice);
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
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            _ = (await first.SendAsync(request)).Value;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "pricing", request.RequestId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelPricingWork(request.RequestId, 0)) : null;
                var claim = cancelFirst ? null : first.SendAsync(new ClaimPricingWork());
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelPricingWork(request.RequestId, 0));
                    claim ??= second.SendAsync(new ClaimPricingWork());
                    if (cancelFirst) { Assert.Null((await claim).Value); }
                    else { await pause.WaitForWaitersAsync(2); }
                }
                finally { await pause.ReleaseAsync(); }
                Assert.Equal(cancelFirst, (await cancellation!).IsSuccess);
                if (!cancelFirst)
                {
                    Assert.Equal("pricing.cancel_conflict", (await cancellation!).Error.Code);
                    Assert.NotNull((await claim!).Value);
                }
            }
            var status = (await first.QueryAsync(new GetRecalculation(request.RequestId))).Value;
            Assert.Equal(cancelFirst ? "Cancelled" : "Running", status.State);
            Assert.Equal(cancelFirst ? 0 : 1, status.Epoch);
            Assert.Equal(cancelFirst ? 0 : 1, status.History.Count);
            Assert.Null((await second.SendAsync(new ClaimPricingWork())).Value);
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
            var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
            _ = (await first.SendAsync(request)).Value;
            var lease = (await first.SendAsync(new ClaimPricingWork())).Value!;
            await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "pricing", lease.TaskId))
            {
                var cancellation = cancelFirst ? first.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch)) : null;
                var failure = cancelFirst ? null : first.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch));
                try
                {
                    await pause.WaitForWriterAsync();
                    cancellation ??= second.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch));
                    failure ??= second.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch));
                    await pause.WaitForWaitersAsync(2);
                }
                finally { await pause.ReleaseAsync(); }
                Assert.True((await cancellation!).IsSuccess);
                Assert.Equal(!cancelFirst, (await failure!).Value);
            }
            var status = (await first.QueryAsync(new GetRecalculation(lease.TaskId))).Value;
            Assert.Equal("Cancelled", status.State);
            Assert.Equal(cancelFirst ? "Cancelled" : "Failed", Assert.Single(status.History).Outcome);
            Assert.Equal(lease.Epoch, status.Epoch);
            Assert.Null((await second.SendAsync(new ClaimPricingWork())).Value);
            Assert.Null((await first.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
        }
    }

    [PostgresFact]
    public async Task FailedWork_CannotBeCancelledUntilRetryCommits_AndNewIntentNeedsANewIdentity()
    {
        await using var application = CreateApplication(new PricingTaskOptions { MaxAttempts = 1 });
        await using var firstScope = application.CreateAsyncScope();
        await using var secondScope = application.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ISender>();
        var second = secondScope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        var accepted = (await first.SendAsync(request)).Value;
        var lease = (await first.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await first.SendAsync(new FailPricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("pricing.cancel_conflict", (await second.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch))).Error.Code);
        await using (var pause = await PostgresTaskBarrier.InstallAsync(database.ConnectionString, "pricing", lease.TaskId))
        {
            var retry = first.SendAsync(new RetryPricingWork(lease.TaskId, lease.Epoch));
            Task<NexusStackNext.BuildingBlocks.Domain.Result<RecalculationStatus>>? cancel = null;
            try
            {
                await pause.WaitForWriterAsync();
                cancel = second.SendAsync(new CancelPricingWork(lease.TaskId, lease.Epoch));
                await pause.WaitForWaitersAsync(2);
            }
            finally { await pause.ReleaseAsync(); }
            Assert.True((await retry).IsSuccess);
            Assert.True((await cancel!).IsSuccess);
        }
        var cancelled = (await first.QueryAsync(new GetRecalculation(lease.TaskId))).Value;
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(accepted.CreatedAt, cancelled.CreatedAt);
        Assert.Equal(0, cancelled.Attempts);
        Assert.Equal("Failed", Assert.Single(cancelled.History).Outcome);
        Assert.Equal("pricing.retry_conflict", (await first.SendAsync(new RetryPricingWork(lease.TaskId, lease.Epoch))).Error.Code);
        Assert.Equal("Cancelled", (await first.SendAsync(request)).Value.State);
        var sheet = (await first.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
        var next = (await first.SendAsync(request with { RequestId = Guid.NewGuid(), ExpectedVersion = sheet.Version })).Value;
        var freshLease = (await second.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(next.TaskId, freshLease.TaskId);
        Assert.True((await second.SendAsync(new CompletePricingWork(freshLease.TaskId, freshLease.Epoch))).Value);
        Assert.Equal("Cancelled", (await first.QueryAsync(new GetRecalculation(lease.TaskId))).Value.State);
    }

    [PostgresFact]
    public async Task ManualDelay_RejectsInvalidBoundsWithoutInputs_AndAcceptsThirtyDaysForCostAndFee()
    {
        await using var application = CreateApplication();
        await using var scope = application.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        foreach (var delay in new[] { -1, 2_592_001 })
        {
            Assert.Equal("pricing.invalid_input", (await sender.SendAsync(request with { DelaySeconds = delay })).Error.Code);
            Assert.Equal("pricing.not_found", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Error.Code);
            Assert.Equal("pricing.not_found", (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Error.Code);
        }
        var accepted = (await sender.SendAsync(request with { DelaySeconds = 2_592_000 })).Value;
        Assert.NotNull(accepted.CreatedAt);
        Assert.Equal(TimeSpan.FromDays(30), accepted.AvailableAt - accepted.CreatedAt.Value);
        var original = (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value;
        var feeRequest = new UpdatePricingFee(Guid.NewGuid(), request.ItemId, original.Version, 0.3m);
        foreach (var delay in new[] { -1, 2_592_001 })
        {
            Assert.Equal("pricing.invalid_input", (await sender.SendAsync(feeRequest with { DelaySeconds = delay })).Error.Code);
            Assert.Equal("pricing.not_found", (await sender.QueryAsync(new GetRecalculation(feeRequest.RequestId))).Error.Code);
            Assert.Equal(original, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value);
        }
        var acceptedFee = (await sender.SendAsync(feeRequest with { DelaySeconds = 2_592_000 })).Value;
        Assert.NotNull(acceptedFee.CreatedAt);
        Assert.Equal(TimeSpan.FromDays(30), acceptedFee.AvailableAt - acceptedFee.CreatedAt.Value);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
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

    private ServiceProvider CreateApplication(PricingTaskOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString, options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
