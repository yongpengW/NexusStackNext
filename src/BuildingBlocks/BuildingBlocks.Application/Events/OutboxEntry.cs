namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// Outbox 中的一条待投递消息。
/// <para>
/// 它与聚合的改动在<b>同一个事务</b>里写入——这样"业务改了但消息没发"和"消息发了但业务没改"
/// 都不会发生。参照仓库是先 <c>Insert</c> 再 <c>Publish</c>，发布失败就留下永久 Pending 的孤儿任务
/// （<c>AsyncTaskService.cs:47-54</c>）。
/// </para>
/// </summary>
public sealed record OutboxEntry
{
    /// <summary>消息标识（等于事件的 <c>EventId</c>），消费端据此去重。</summary>
    public required Guid Id { get; init; }

    /// <summary>事件名。</summary>
    public required string EventName { get; init; }

    /// <summary>已序列化的载荷。</summary>
    public required string Payload { get; init; }

    /// <summary>事件发生时刻（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>已尝试投递的次数。</summary>
    public int AttemptCount { get; init; }

    /// <summary>下次可尝试的时间；<c>null</c> 表示立即可投。</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>投递成功时间；<c>null</c> 表示尚未成功。</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>进入死信的时间；<c>null</c> 表示未进死信。</summary>
    public DateTimeOffset? DeadLetteredAt { get; init; }

    /// <summary>最后一次失败原因，便于排查。</summary>
    public string? LastFailure { get; init; }

    /// <summary>是否已投递成功。</summary>
    public bool IsDelivered => DeliveredAt is not null;

    /// <summary>是否已进入死信。</summary>
    public bool IsDeadLettered => DeadLetteredAt is not null;

    /// <summary>是否仍在等待投递。</summary>
    public bool IsPending => !IsDelivered && !IsDeadLettered;

    /// <summary>由领域/集成事件构造一条待投递记录。</summary>
    /// <param name="event">集成事件。</param>
    /// <param name="serializer">序列化器。</param>
    /// <returns>Outbox 记录。</returns>
    public static OutboxEntry From(IntegrationEvent @event, IIntegrationEventSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(serializer);

        return new OutboxEntry
        {
            Id = @event.EventId,
            EventName = @event.EventName,
            Payload = serializer.Serialize(@event),
            OccurredAt = @event.OccurredAt,
        };
    }

    /// <summary>转换成投递到总线上的封套。</summary>
    /// <returns>消息封套。</returns>
    public EventEnvelope ToEnvelope() => new()
    {
        MessageId = Id,
        EventName = EventName,
        Payload = Payload,
        OccurredAt = OccurredAt,
    };

    /// <summary>记录一次失败：递增尝试次数、写入下次尝试时间与原因。</summary>
    /// <param name="failure">失败原因。</param>
    /// <param name="nextAttemptAt">下次尝试时间。</param>
    /// <returns>更新后的记录。</returns>
    public OutboxEntry RecordFailure(string failure, DateTimeOffset nextAttemptAt) => this with
    {
        AttemptCount = AttemptCount + 1,
        NextAttemptAt = nextAttemptAt,
        LastFailure = failure,
    };

    /// <summary>标记为已投递。</summary>
    /// <param name="now">当前时间。</param>
    /// <returns>更新后的记录。</returns>
    public OutboxEntry MarkDelivered(DateTimeOffset now) => this with
    {
        DeliveredAt = now,
        LastFailure = null,
    };

    /// <summary>标记为死信。</summary>
    /// <param name="failure">最终失败原因。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>更新后的记录。</returns>
    public OutboxEntry MarkDeadLettered(string failure, DateTimeOffset now) => this with
    {
        AttemptCount = AttemptCount + 1,
        DeadLetteredAt = now,
        LastFailure = failure,
    };
}
