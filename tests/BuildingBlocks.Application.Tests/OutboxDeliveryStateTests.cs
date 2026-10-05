using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

public sealed class OutboxDeliveryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static OutboxEntry Pending() => new()
    {
        Id = Guid.NewGuid(),
        EventName = "sample.changed.v1",
        Payload = "{}",
        OccurredAt = Now,
    };

    [Fact]
    public void RetryAtMaximumRevision_RefusesOverflow_AndKeepsTheOriginalEnvelopeAndConfirmationPriority()
    {
        var pending = Pending() with { RetryRevision = long.MaxValue };
        var stopped = pending.MarkDeadLettered("controlled-maximum-stop", Now);
        Assert.Null(stopped.RetryDelivery(Now));
        Assert.Equal(long.MaxValue, stopped.RetryRevision);
        Assert.Equal(pending.ToEnvelope(), stopped.ToEnvelope());
        var delivered = stopped.MarkDelivered(Now.AddSeconds(1));
        Assert.True(delivered.IsDelivered);
        Assert.False(delivered.IsDeadLettered);
        Assert.Equal(long.MaxValue, delivered.RetryRevision);
        Assert.Equal(pending.ToEnvelope(), delivered.ToEnvelope());
        Assert.Equal(delivered, delivered.MarkDeadLettered("late-maximum-stop", Now));
        Assert.Null(delivered.RetryDelivery(Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulConfirmation_WinsRegardlessOfFailureArrivalOrder(bool failureFirst)
    {
        var entry = Pending();
        if (failureFirst) { entry = entry.MarkDeadLettered("unavailable", Now); }
        var confirmed = entry.MarkDelivered(Now.AddSeconds(1));

        var late = confirmed.RecordFailure("late failure", Now.AddMinutes(1))
            .MarkDeadLettered("late stop", Now.AddSeconds(2)).MarkDelivered(Now.AddSeconds(3));

        Assert.Equal(confirmed, late);
        Assert.True(late.IsDelivered);
        Assert.False(late.IsDeadLettered);
        Assert.Null(late.NextAttemptAt);
        Assert.Null(late.LastFailure);
        Assert.Equal(Now.AddSeconds(1), late.DeliveredAt);
        Assert.Null(late.RetryDelivery(Now));
    }

    [Fact]
    public void Retry_RequiresTheObservedStopAndPreservesTheOriginalMessage()
    {
        var pending = Pending();
        Assert.Null(pending.RetryDelivery(Now));
        var stopped = pending.MarkDeadLettered("unavailable", Now);
        Assert.Equal(stopped, stopped.RecordFailure("late failure", Now.AddMinutes(1)));
        Assert.Null(stopped.RetryDelivery(Now.AddSeconds(1)));

        var retried = Assert.IsType<OutboxEntry>(stopped.RetryDelivery(Now));
        Assert.Equal(pending.ToEnvelope(), retried.ToEnvelope());
        Assert.Equal(1, retried.RetryRevision);
        Assert.Equal(0, retried.AttemptCount);
        Assert.Null(retried.RetryDelivery(Now));
    }
}
