using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

public sealed class MemoryFactDeliveryRecoveryStoreTests
{
    [Fact]
    public async Task UnrepresentableOffsetDeadline_IsRejectedBeforePreparingOrPublishingRecovery()
    {
        var (now, original, messages, capacity, recovery) = CreateStoppedSource(0);
        var beforeState = (await recovery.GetAsync(original.Id)).Value;
        var beforeControl = (await recovery.ReadRecoveryCapacityAsync()).Value;
        var beforeFacts = capacity.Read("sample").Value;
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, 0, "manual-retry");
        foreach (var invalidTime in new[] { DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue.AddDays(-7).ToOffset(TimeSpan.FromHours(14)),
            new DateTimeOffset(DateTime.MaxValue.AddDays(-7), TimeSpan.FromHours(-14)) })
        {
            var refused = await recovery.RecoverAsync(request, "original-operator", invalidTime, null);
            Assert.Equal("recovery.invalid", refused.Error.Code);
            Assert.Equal(beforeState, (await recovery.GetAsync(original.Id)).Value);
            Assert.Equal(beforeControl, (await recovery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal(beforeFacts, capacity.Read("sample").Value);
            Assert.Equal("recovery.not_found", (await recovery.GetRecoveryAsync(request.RequestId)).Error.Code);
            Assert.Equal(original, Assert.Single(messages).Value);
        }
        var accepted = await recovery.RecoverAsync(request, "original-operator", now, null);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), accepted.Value.RetainUntil);
    }

    [Fact]
    public async Task MaximumRetryRevision_RefusesRecoveryWithoutOverflowOrPartialPublication()
    {
        var (now, original, messages, capacity, recovery) = CreateStoppedSource(long.MaxValue);
        var beforeState = (await recovery.GetAsync(original.Id)).Value;
        var beforeControl = (await recovery.ReadRecoveryCapacityAsync()).Value;
        var beforeFacts = capacity.Read("sample").Value;
        Assert.Equal(long.MaxValue, beforeState.RetryRevision);
        Assert.Equal("DeadLettered", beforeState.State);
        Assert.Equal(1, beforeFacts.RetainedRecords);
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, now, long.MaxValue, "manual-retry");
        Assert.True(request.IsValid);
        var refused = await recovery.RecoverAsync(request, "original-operator", now, null);
        Assert.Equal("recovery.conflict", refused.Error.Code);
        Assert.Equal(beforeState, (await recovery.GetAsync(original.Id)).Value);
        Assert.Equal(beforeControl, (await recovery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(beforeFacts, capacity.Read("sample").Value);
        Assert.Equal("recovery.not_found", (await recovery.GetRecoveryAsync(request.RequestId)).Error.Code);
        Assert.Equal(original, Assert.Single(messages).Value);
    }

    private static (DateTimeOffset Now, OutboxEntry Original, Dictionary<Guid, OutboxEntry> Messages,
        InMemoryCommittedFactCapacity Capacity, InMemoryFactDeliveryRecoveryStore Recovery) CreateStoppedSource(long revision)
    {
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var original = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            EventName = "sample.business.v1",
            Payload = "{}",
            OccurredAt = now,
            DeadLetteredAt = now,
            RetryRevision = revision,
            AttemptCount = 3,
            LastFailure = "controlled-protocol-stop",
        };
        var messages = new Dictionary<Guid, OutboxEntry>();
        var capacity = new InMemoryCommittedFactCapacity(new Lock(), original.EventName);
        Assert.True(capacity.TryCommit([original], () => messages.Add(original.Id, original)));
        var source = new FactDeliveryRecoverySource("sample", original.EventName, "sample.policy.v1", new(
            new("recovery.invalid", "Invalid"), new("recovery.conflict", "Conflict"), new("recovery.request_conflict", "Request conflict"),
            new("recovery.not_found", "Not found"), new("recovery.exhausted", "Exhausted"), new("recovery.unmanaged", "Unmanaged"),
            new("recovery.delivery_not_found", "Delivery not found")));
        var recovery = new InMemoryFactDeliveryRecoveryStore(capacity, () => messages, source);
        return (now, original, messages, capacity, recovery);
    }
}
