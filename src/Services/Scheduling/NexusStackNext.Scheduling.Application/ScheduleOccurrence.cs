using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Scheduling.Application;

/// <summary>某个计划已经登记的不可变触发事实，与计划及 Outbox 一起保存。</summary>
/// <param name="OccurrenceId">稳定发生和消息标识。</param>
/// <param name="PlanId">计划。</param>
/// <param name="TriggerSequence">计划内发生序号。</param>
/// <param name="ScheduledAt">原计划时刻。</param>
/// <param name="TriggeredAt">实际登记时刻。</param>
/// <param name="TargetKind">目标操作。</param>
/// <param name="TargetId">目标对象。</param>
/// <param name="CreatedBy">委托人。</param>
public sealed record ScheduleOccurrence(Guid OccurrenceId, long PlanId, long TriggerSequence, DateTimeOffset ScheduledAt,
    DateTimeOffset TriggeredAt, string TargetKind, Guid TargetId, string CreatedBy)
{
    /// <summary>产生对外契约；使用既有发生身份，不在重试时创建消息身份。</summary>
    /// <returns>线路事件。</returns>
    public ScheduleTriggeredV1 ToEvent() => new()
    {
        EventId = OccurrenceId,
        PlanId = PlanId,
        TriggerSequence = TriggerSequence,
        ScheduledAt = ScheduledAt,
        OccurredAt = TriggeredAt,
        TargetKind = TargetKind,
        TargetId = TargetId,
        CreatedBy = CreatedBy,
    };
}

/// <summary>触发事实及其最小交付状态；交付不表示业务接受或执行成功。</summary>
/// <param name="OccurrenceId">发生标识。</param>
/// <param name="TriggerSequence">发生序号。</param>
/// <param name="ScheduledAt">原计划时刻。</param>
/// <param name="TriggeredAt">实际时刻。</param>
/// <param name="TargetKind">目标操作。</param>
/// <param name="TargetId">目标对象。</param>
/// <param name="DeliveryState">Pending / Delivered / DeadLettered。</param>
/// <param name="AttemptCount">失败次数。</param>
/// <param name="NextAttemptAt">下次交付尝试。</param>
/// <param name="DeadLetteredAt">失败预算耗尽的时刻。</param>
public sealed record ScheduleOccurrenceDelivery(Guid OccurrenceId, long TriggerSequence, DateTimeOffset ScheduledAt,
    DateTimeOffset TriggeredAt, string TargetKind, Guid TargetId, string DeliveryState, int AttemptCount,
    DateTimeOffset? NextAttemptAt, DateTimeOffset? DeadLetteredAt)
{
    /// <summary>观察到的停止交付状态已经改变。</summary>
    public static readonly Error Conflict = new("scheduling.delivery_conflict", "交付状态已经改变，请重新读取。");

    /// <summary>按已观察的停止时刻恢复原消息，两个适配器遵循相同规则。</summary>
    /// <param name="entry">已锁定的当前 Outbox。</param>
    /// <param name="expectedDeadLetteredAt">调用方观察到的停止时刻。</param>
    /// <returns>同一消息的新投递状态，或冲突。</returns>
    public static Result<OutboxEntry> Retry(OutboxEntry? entry, DateTimeOffset expectedDeadLetteredAt) =>
        entry?.EventName == ScheduleTriggeredV1.Name && entry.RetryDelivery(expectedDeadLetteredAt) is { } retried
            ? Result.Success(retried) : Result.Failure<OutboxEntry>(Conflict);

    /// <summary>裁剪掉载荷及底层错误。</summary>
    /// <param name="occurrence">触发事实。</param>
    /// <param name="entry">其 Outbox。</param>
    /// <returns>可公开的状态。</returns>
    public static ScheduleOccurrenceDelivery From(ScheduleOccurrence occurrence, OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(entry);
        return new(occurrence.OccurrenceId, occurrence.TriggerSequence, occurrence.ScheduledAt, occurrence.TriggeredAt,
            occurrence.TargetKind, occurrence.TargetId, entry.IsDelivered ? "Delivered" : entry.IsDeadLettered ? "DeadLettered" : "Pending",
            entry.AttemptCount, entry.NextAttemptAt, entry.DeadLetteredAt);
    }
}

/// <summary>有界触发历史。</summary>
/// <param name="Items">当前页。</param>
/// <param name="Total">计划的总发生次数。</param>
public sealed record ScheduleOccurrencePage(IReadOnlyList<ScheduleOccurrenceDelivery> Items, long Total);
