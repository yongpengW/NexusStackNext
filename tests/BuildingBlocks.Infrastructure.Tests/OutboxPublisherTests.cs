using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// Outbox 投递器。这里每一条都对应参照仓库的一个具体缺陷或一条明确的取舍。
/// </summary>
public sealed class OutboxPublisherTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string EventName = "ordering.order-placed.v1";

    private static OutboxEntry NewEntry(
        string eventName = EventName,
        int attemptCount = 0,
        DateTimeOffset? nextAttemptAt = null) => new()
        {
            Id = Guid.NewGuid(),
            EventName = eventName,
            Payload = """{"orderId":"00000000-0000-0000-0000-000000000001"}""",
            OccurredAt = Start,
            AttemptCount = attemptCount,
            NextAttemptAt = nextAttemptAt,
        };

    private static (OutboxPublisher Publisher, FakeOutboxStore Store, FakeEventBus Bus, MutableClock Clock) Build(
        OutboxDeliveryOptions? options = null)
    {
        var store = new FakeOutboxStore();
        var bus = new FakeEventBus();
        var clock = new MutableClock(Start);
        var publisher = new OutboxPublisher(store, bus, clock, options ?? OutboxDeliveryOptions.Default);
        return (publisher, store, bus, clock);
    }

    [Fact]
    public async Task PublishPending_Success_MarksDelivered()
    {
        var (publisher, store, bus, _) = Build();
        var entry = NewEntry();
        store.Seed(entry);

        var result = await publisher.PublishPendingAsync();

        Assert.Equal(new OutboxRunResult(1, 1, 0, 0), result);
        Assert.Single(bus.Published);
        Assert.True(store.Entries[0].IsDelivered);
        Assert.Equal(entry.Id, bus.Published[0].MessageId);
        Assert.Equal(EventName, bus.Published[0].EventName);
        Assert.Equal("ordering.order-placed.v1", bus.Published[0].RoutingKey);
    }

    [Fact]
    public async Task PublishPending_Unroutable_IsFailure_NotSilentSuccess()
    {
        // 参照仓库的 BasicReturn 只记日志、发布后照样 ACK（EventPublisher.cs:36-41/505-510），
        // 于是消息静默丢失而调用方以为发成功了。这里必须当成失败。
        var (publisher, store, bus, _) = Build();
        store.Seed(NewEntry());
        bus.UnroutableEventNames.Add(EventName);

        var result = await publisher.PublishPendingAsync();

        Assert.Equal(new OutboxRunResult(1, 0, 1, 0), result);
        Assert.False(store.Entries[0].IsDelivered);
        Assert.Contains("unroutable", store.Entries[0].LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPending_SecondRun_DoesNotRepublishDeliveredEntries()
    {
        var (publisher, store, bus, _) = Build();
        store.Seed(NewEntry());

        await publisher.PublishPendingAsync();
        var second = await publisher.PublishPendingAsync();

        Assert.Equal(new OutboxRunResult(0, 0, 0, 0), second);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task PublishPending_Failure_SchedulesRetry_AndSkipsUntilDue()
    {
        var (publisher, store, bus, clock) = Build();
        store.Seed(NewEntry());
        bus.UnroutableEventNames.Add(EventName);

        await publisher.PublishPendingAsync();

        var afterFailure = store.Entries[0];
        Assert.Equal(1, afterFailure.AttemptCount);
        Assert.Equal(Start + OutboxDeliveryOptions.Default.BackoffFor(1), afterFailure.NextAttemptAt);

        // 还没到时间：不投递。
        Assert.Equal(new OutboxRunResult(0, 0, 0, 0), await publisher.PublishPendingAsync());
        Assert.Single(bus.Published);

        // 到时间后重试。
        clock.Advance(OutboxDeliveryOptions.Default.BackoffFor(1));
        Assert.Equal(new OutboxRunResult(1, 0, 1, 0), await publisher.PublishPendingAsync());
        Assert.Equal(2, store.Entries[0].AttemptCount);
    }

    [Fact]
    public async Task PublishPending_ExceedingMaxAttempts_GoesToDeadLetter_AndIsNeverRetried()
    {
        var options = OutboxDeliveryOptions.Default with { MaxAttempts = 2 };
        var (publisher, store, bus, clock) = Build(options);
        store.Seed(NewEntry(attemptCount: 1));
        bus.UnroutableEventNames.Add(EventName);

        var result = await publisher.PublishPendingAsync();

        Assert.Equal(new OutboxRunResult(1, 0, 0, 1), result);
        Assert.True(store.Entries[0].IsDeadLettered);
        Assert.False(store.Entries[0].IsPending);

        // 死信之后即使时间推进也不会再投递。
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new OutboxRunResult(0, 0, 0, 0), await publisher.PublishPendingAsync());
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task PublishPending_OneBadMessage_DoesNotBlockTheRest()
    {
        const string badEventName = "ordering.other-event.v1";
        var (publisher, store, bus, _) = Build();
        store.Seed(NewEntry(), NewEntry(badEventName), NewEntry());
        bus.UnroutableEventNames.Add(badEventName);

        var result = await publisher.PublishPendingAsync();

        // 三条都会被尝试：一条失败不会中断它后面的消息。
        Assert.Equal(new OutboxRunResult(3, 2, 1, 0), result);
        Assert.Equal(3, bus.Published.Count);
        Assert.Equal(2, store.Entries.Count(static entry => entry.IsDelivered));
        Assert.Equal(1, store.Entries.Count(static entry => entry.AttemptCount == 1 && !entry.IsDelivered));
    }

    [Fact]
    public async Task PublishPending_BusThrows_TreatedAsFailure()
    {
        var (publisher, store, bus, _) = Build();
        store.Seed(NewEntry());
        bus.ThrowOnPublish = new TimeoutException("broker 没响应");

        var result = await publisher.PublishPendingAsync();

        Assert.Equal(new OutboxRunResult(1, 0, 1, 0), result);
        Assert.Contains("broker 没响应", store.Entries[0].LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPending_Cancellation_Propagates()
    {
        // 取消不是投递失败——不能把它记成一次失败尝试，否则会白烧一次重试配额。
        var (publisher, store, bus, _) = Build();
        store.Seed(NewEntry());
        using var cts = new CancellationTokenSource();
        bus.ThrowOnPublish = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishPendingAsync(cts.Token));

        Assert.Equal(0, store.Entries[0].AttemptCount);
        Assert.False(store.Entries[0].IsDelivered);
    }

    [Fact]
    public async Task PublishPending_EmptyStore_ReturnsZeroes()
    {
        var (publisher, _, _, _) = Build();

        Assert.Equal(new OutboxRunResult(0, 0, 0, 0), await publisher.PublishPendingAsync());
    }

    [Fact]
    public async Task PublishPending_RespectsBatchSize()
    {
        var options = OutboxDeliveryOptions.Default with { BatchSize = 2 };
        var (publisher, store, _, _) = Build(options);
        store.Seed(NewEntry(), NewEntry(), NewEntry(), NewEntry(), NewEntry());

        var result = await publisher.PublishPendingAsync();

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, result.Delivered);
    }

    [Fact]
    public void BackoffFor_GrowsThenReusesTheLastTier()
    {
        var options = OutboxDeliveryOptions.Default;

        Assert.True(options.BackoffFor(2) > options.BackoffFor(1));
        Assert.Equal(options.BackoffFor(options.BackoffSchedule.Count), options.BackoffFor(999));
    }

    [Fact]
    public void Constructor_RejectsInvalidOptions()
    {
        var store = new FakeOutboxStore();
        var bus = new FakeEventBus();
        var clock = new MutableClock(Start);

        Assert.Throws<ArgumentNullException>(() => new OutboxPublisher(null!, bus, clock, OutboxDeliveryOptions.Default));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OutboxPublisher(store, bus, clock, OutboxDeliveryOptions.Default with { BatchSize = 0 }));
        Assert.Throws<InvalidOperationException>(
            () => new OutboxPublisher(store, bus, clock, OutboxDeliveryOptions.Default with { BackoffSchedule = [] }));
    }
}
