using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactRecoveryFailureTests
{
    [Fact]
    public Task Identity_RecoveryWaitsOnlyItsOwnedBudget_WithoutPublishingPartialState()
        => VerifyContentionAsync("identity", cancel: false);

    [Fact]
    public Task Identity_CallerCancellationEndsRecovery_WithoutPublishingPartialState()
        => VerifyContentionAsync("identity", cancel: true);

    [Theory]
    [InlineData("platform", false)]
    [InlineData("files", false)]
    [InlineData("scheduling", false)]
    [InlineData("platform", true)]
    [InlineData("files", true)]
    [InlineData("scheduling", true)]
    public Task OtherPlatformSources_RecoveryPreservesOwnedState_OnBusyOrCallerCancellation(string source, bool cancel)
        => VerifyContentionAsync(source, cancel);

    private static async Task VerifyContentionAsync(string source, bool cancel)
    {
        using var clock = new PausingClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        await using var app = new MemoryBudgetApp(clock, source, root: true) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var now = clock.UtcNow;
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source);
        var initial = (await policies.ReadPolicyAsync()).Value;
        var changed = await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-operator", now, null);
        Assert.True(changed.IsSuccess);
        Assert.NotNull(changed.Value.EventId);
        var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
        var original = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == changed.Value.EventId);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-lock-stop", now, 0));
        var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
        var stopped = (await delivery.GetAsync(original.Id)).Value;
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var beforePolicy = (await policies.ReadPolicyAsync()).Value;
        var facts = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(source);
        var beforeFacts = (await facts.ReadAsync()).Value;
        var otherPending = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "dependency-restored");
        foreach (var invalidTime in new[] { DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue.AddDays(-7).ToOffset(TimeSpan.FromHours(14)),
            new DateTimeOffset(DateTime.MaxValue.AddDays(-7), TimeSpan.FromHours(-14)) })
        {
            Assert.Equal($"{source}.delivery_recovery.invalid",
                (await delivery.RecoverAsync(request, "recovery-operator", invalidTime, null)).Error.Code);
            Assert.Equal(stopped, (await delivery.GetAsync(original.Id)).Value);
            Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(beforeFacts, (await facts.ReadAsync()).Value);
            Assert.Equal(otherPending, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
        }
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(source);
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? attempting = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var recovering = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                return await delivery.RecoverAsync(request, "recovery-operator", now, null, cancellation.Token);
            });
            attempting = recovering;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovering.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(cancellation.IsCancellationRequested);
            }
            else
            {
                Assert.Equal("audit_capacity.busy", (await recovering.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code);
                Assert.False(cancellation.IsCancellationRequested);
            }
            Assert.True(recovering.IsCompleted);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (attempting is not null)
            {
                try { await attempting; }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            }
        }
        Assert.Equal(stopped, (await delivery.GetAsync(original.Id)).Value);
        Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(beforeFacts, (await facts.ReadAsync()).Value);
        Assert.Equal(otherPending, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal($"{source}.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
        var recovered = await delivery.RecoverAsync(request, "recovery-operator", now, null);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(1, recovered.Value.RetryRevision);
        Assert.Equal(recovered.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.EventName, pending.EventName);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        Assert.Equal(beforeRecovery.Capacity.RetainedRecords + 1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(beforeFacts, (await facts.ReadAsync()).Value);
        Assert.Equal(otherPending, (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Where(entry => entry.Id != original.Id));
    }
}
