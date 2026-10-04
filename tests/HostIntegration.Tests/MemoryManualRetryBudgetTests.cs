using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryManualRetryBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingDeadLetterRetry_PreservesConditionAndCapacity_OnBusyOrCancellation(bool cancel)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("platform");
        var now = clock.UtcNow;
        var key = SettingKey.Create("budget.manual-retry").Value;
        Assert.True((await settings.WriteAsync(key, "value")).IsSuccess);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now));
        Assert.True(await outbox.MarkFailedAsync(original.Id, "first-failure", now.AddMinutes(1), original.RetryRevision));
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "stopped", now, original.RetryRevision));
        var stopped = Assert.Single(await delivery.ListAsync("DeadLettered", 10));
        Assert.Equal(2, stopped.Attempts);
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? attempt = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retrying = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                started.SetResult();
                return await delivery.RetryAsync(original.Id, stopped.DeadLetteredAt!.Value, cancellation.Token);
            });
            attempt = retrying;
            await started.Task;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await retrying.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else { Assert.Equal("audit_capacity.busy", (await retrying.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code); }
        }
        finally
        {
            await holder.ReleaseAsync();
            if (attempt is not null)
            {
                try { await attempt; }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            }
        }
        Assert.Equal(stopped, Assert.Single(await delivery.ListAsync("DeadLettered", 10)));
        Assert.Empty(await outbox.ReadPendingAsync(10, now.AddDays(1)));
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Equal(1, (await settings.GetAsync(key))!.Version);
        Assert.Equal("platform.delivery_conflict", (await delivery.RetryAsync(original.Id, now.AddSeconds(1))).Error.Code);
        Assert.Equal(stopped, Assert.Single(await delivery.ListAsync("DeadLettered", 10)));
        var recovered = await delivery.RetryAsync(original.Id, stopped.DeadLetteredAt!.Value);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(original.Id, recovered.Value.MessageId);
        Assert.Equal("Pending", recovered.Value.State);
        Assert.Equal(0, recovered.Value.Attempts);
        var pending = Assert.Single(await outbox.ReadPendingAsync(10, now));
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        Assert.False(await outbox.MarkFailedAsync(original.Id, "stale-failure", now, original.RetryRevision));
        Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "stale-dead-letter", now, original.RetryRevision));
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal("platform.delivery_conflict", (await delivery.RetryAsync(original.Id, stopped.DeadLetteredAt.Value)).Error.Code);
        await outbox.MarkDeliveredAsync(original.Id, now);
        Assert.Equal("Delivered", Assert.Single(await delivery.ListAsync("Delivered", 10)).State);
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccurrenceDeadLetterRetry_KeepsOriginalOccurrence_AndDoesNotChargeFactQuota(bool cancel)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Scheduling") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var now = clock.UtcNow;
        var plan = ScheduledTask.Create(new ScheduledTaskId(97801), TaskCode.Create("manual-retry").Value, TimeSpan.FromHours(1), now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "owner").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Advance(now, now.AddHours(1), trigger: true).IsSuccess);
        var id = Guid.NewGuid();
        var occurrence = new ScheduleOccurrence(id, plan.Id.Value, plan.TriggerSequence, now, now, plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
        var decision = new ScheduleDecision(id, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule, "Triggered", now, now, now.AddHours(1), id);
        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, now)).Count);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now), entry => entry.EventName == ScheduleTriggeredV1.Name);
        Assert.True(await outbox.MarkFailedAsync(id, "first-failure", now.AddMinutes(1), original.RetryRevision));
        Assert.True(await outbox.MarkDeadLetteredAsync(id, "stopped", now, original.RetryRevision));
        var stopped = Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal("DeadLettered", stopped.DeliveryState);
        Assert.Equal(2, stopped.AttemptCount);
        var beforeCapacity = (await capacity.ReadAsync()).Value;
        Assert.Equal(2, beforeCapacity.RetainedRecords);
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? attempt = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retrying = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                started.SetResult();
                return await store.RetryOccurrenceAsync(id, stopped.DeadLetteredAt!.Value, cancellation.Token);
            });
            attempt = retrying;
            await started.Task;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await retrying.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else { Assert.Equal("audit_capacity.busy", (await retrying.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code); }
        }
        finally
        {
            await holder.ReleaseAsync();
            if (attempt is not null)
            {
                try { await attempt; }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            }
        }
        Assert.Equal(stopped, Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items));
        Assert.Equal(1, (await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Total);
        Assert.Equal(2, (await store.FindAsync(plan.Id))!.Version);
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Equal("scheduling.delivery_conflict", (await store.RetryOccurrenceAsync(id, now.AddSeconds(1))).Error.Code);
        var recovered = await store.RetryOccurrenceAsync(id, stopped.DeadLetteredAt!.Value);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(id, recovered.Value.OccurrenceId);
        Assert.Equal("Pending", recovered.Value.DeliveryState);
        Assert.Equal(0, recovered.Value.AttemptCount);
        var pending = Assert.Single(await outbox.ReadPendingAsync(10, now), entry => entry.EventName == ScheduleTriggeredV1.Name);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        Assert.False(await outbox.MarkFailedAsync(id, "stale-failure", now, original.RetryRevision));
        Assert.False(await outbox.MarkDeadLetteredAsync(id, "stale-dead-letter", now, original.RetryRevision));
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(10, now), entry => entry.EventName == ScheduleTriggeredV1.Name));
        Assert.Equal("scheduling.delivery_conflict", (await store.RetryOccurrenceAsync(id, stopped.DeadLetteredAt.Value)).Error.Code);
        await outbox.MarkDeliveredAsync(id, now);
        Assert.Equal("Delivered", Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items).DeliveryState);
        Assert.Equal(beforeCapacity, (await capacity.ReadAsync()).Value);
        Assert.Equal(1, (await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Total);
        Assert.Equal(2, (await store.FindAsync(plan.Id))!.Version);
    }
}
