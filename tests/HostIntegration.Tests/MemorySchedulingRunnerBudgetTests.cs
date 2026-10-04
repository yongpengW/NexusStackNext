using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemorySchedulingRunnerBudgetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OriginReadBusyOrCancelled_DoesNotDefer_WhenContentionEndsBeforeFallback(bool observed, bool cancel)
    {
        var instant = DateTimeOffset.UtcNow;
        using var clock = new PausingClock(new DateTimeOffset(instant.UtcTicks - (instant.UtcTicks % 10), TimeSpan.Zero));
        await using var app = new MemoryBudgetApp(clock, "Scheduling") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        PausedFactCleanup? holder = null;
        var pausedStore = new PausingScheduleOriginStore(store, () =>
        {
            if (holder is not null) { holder.ReleaseAsync().GetAwaiter().GetResult(); }
        });
        var runner = new ScheduleRunner(pausedStore, clock, scope.ServiceProvider.GetRequiredService<IScheduleCalendar>(),
            observed ? scope.ServiceProvider.GetRequiredService<IBackgroundExecutionObservation>() : null);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var now = clock.UtcNow;
        var plan = ScheduledTask.Create(new ScheduledTaskId(97804), TaskCode.Create("origin-budget").Value, TimeSpan.FromHours(1), now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "owner").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, now));
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        await using var cancellation = new TimedCallerCancellation();
        var running = Task.Run(() => runner.RunOnceAsync(cancellation.Token));
        try
        {
            await pausedStore.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            holder = new PausedFactCleanup(clock, cleanup);
            await holder.WaitUntilPausedAsync();
            pausedStore.Resume();
            if (cancel)
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                var failed = await running.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, failed.Examined);
                Assert.Equal(0, failed.Triggered);
                Assert.Equal(0, failed.Skipped);
                Assert.Equal(new long[] { plan.Id.Value }, failed.FailedPlanIds);
            }
        }
        finally
        {
            pausedStore.Resume();
            if (holder is not null) { await holder.ReleaseAsync(); }
            try { await running; }
            catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
        }
        var unchanged = Assert.IsType<ScheduledTask>(await store.FindAsync(plan.Id));
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(0, unchanged.TriggerSequence);
        Assert.Null(unchanged.RetryAt);
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        Assert.Equal(2, (await capacity.ReadAsync()).Value.RetainedRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionRegistrationBusyOrCancelled_DoesNotInventSkippedDecisionOrDeferral(bool cancel)
    {
        var instant = DateTimeOffset.UtcNow;
        using var clock = new PausingClock(new DateTimeOffset(instant.UtcTicks - (instant.UtcTicks % 10), TimeSpan.Zero));
        using var serializer = new PausingEventSerializer(new SystemTextJsonIntegrationEventSerializer(), ScheduleTriggeredV1.Name);
        await using var app = new MemoryBudgetApp(clock, "Scheduling", serializer: serializer) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var now = clock.UtcNow;
        var plan = ScheduledTask.Create(new ScheduledTaskId(97802), TaskCode.Create("runner-budget").Value, TimeSpan.FromHours(1), now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "owner").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, now));
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        await using var cancellation = new TimedCallerCancellation();
        var running = Task.Run(() => runner.RunOnceAsync(cancellation.Token));
        PausedFactCleanup? holder = null;
        try
        {
            await serializer.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            holder = new PausedFactCleanup(clock, cleanup);
            await holder.WaitUntilPausedAsync();
            serializer.Resume();
            if (cancel)
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                var failed = await running.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, failed.Examined);
                Assert.Equal(0, failed.Triggered);
                Assert.Equal(0, failed.Skipped);
                Assert.Equal(new long[] { plan.Id.Value }, failed.FailedPlanIds);
            }
        }
        finally
        {
            serializer.Resume();
            if (holder is not null) { await holder.ReleaseAsync(); }
            try { await running; }
            catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
        }
        var unchanged = Assert.IsType<ScheduledTask>(await store.FindAsync(plan.Id));
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(0, unchanged.TriggerSequence);
        Assert.Null(unchanged.RetryAt);
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        var recovered = await runner.RunOnceAsync();
        Assert.Equal(1, recovered.Triggered);
        Assert.Equal(0, recovered.Skipped);
        Assert.Empty(recovered.FailedPlanIds);
        Assert.Equal("Triggered", Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items).Kind);
        Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(2, (await store.FindAsync(plan.Id))!.Version);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, now)).Count);
        Assert.Equal(2, (await capacity.ReadAsync()).Value.RetainedRecords);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, now)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DueScanBusyOrCancelled_PropagatesFailure_AndLeavesDuePlanForRecovery(bool cancel)
    {
        var instant = DateTimeOffset.UtcNow;
        using var clock = new PausingClock(new DateTimeOffset(instant.UtcTicks - (instant.UtcTicks % 10), TimeSpan.Zero));
        await using var app = new MemoryBudgetApp(clock, "Scheduling") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var now = clock.UtcNow;
        var plan = ScheduledTask.Create(new ScheduledTaskId(97803), TaskCode.Create("scan-budget").Value, TimeSpan.FromHours(1), now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "owner").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, now));
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? running = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            running = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                started.SetResult();
                await runner.RunOnceAsync(cancellation.Token);
            });
            await started.Task;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else { await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2))); }
        }
        finally
        {
            await holder.ReleaseAsync();
            if (running is not null)
            {
                try { await running; }
                catch (Exception error) when (error is CommittedFactCapacityBusyException or OperationCanceledException) { }
            }
        }
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Equal(1, (await store.FindAsync(plan.Id))!.Version);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        Assert.Equal(2, (await capacity.ReadAsync()).Value.RetainedRecords);
    }
}
