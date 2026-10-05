namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>事实投递的安全投影，不包含正文或异常，不表示中央处理已完成。</summary>
/// <param name="MessageId">稳定消息标识。</param>
/// <param name="State">Pending / Delivered / DeadLettered。</param>
/// <param name="Attempts">失败次数。</param>
/// <param name="NextAttemptAt">下次尝试时刻。</param>
/// <param name="DeadLetteredAt">停止自动投递时刻。</param>
public sealed record FactDeliveryState(Guid MessageId, string State, int Attempts,
    DateTimeOffset? NextAttemptAt, DateTimeOffset? DeadLetteredAt)
{
    /// <summary>原消息的恢复代次。</summary>
    public long RetryRevision { get; init; }

    /// <summary>从所属消息中裁剪可公开的状态，发布确认优先于停投证据。</summary>
    /// <param name="entry">所属上下文消息。</param>
    /// <returns>安全投递状态。</returns>
    public static FactDeliveryState From(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Id, entry.IsDelivered ? "Delivered" : entry.IsDeadLettered ? "DeadLettered" : "Pending",
            entry.AttemptCount, entry.NextAttemptAt, entry.DeadLetteredAt)
        { RetryRevision = entry.RetryRevision };
    }
}
