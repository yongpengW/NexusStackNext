using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Platform.Contracts;

namespace NexusStackNext.Platform.Application;

/// <summary>最小投递状态，不包含消息正文或底层异常。</summary>
/// <param name="MessageId">稳定消息标识。</param>
/// <param name="State">Pending / Delivered / DeadLettered。</param>
/// <param name="Attempts">失败次数。</param>
/// <param name="NextAttemptAt">下次尝试时刻。</param>
/// <param name="DeadLetteredAt">停止自动重试的时刻，也是人工重试条件。</param>
public sealed record SettingAuditDelivery(Guid MessageId, string State, int Attempts, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeadLetteredAt)
{
    /// <summary>重试所依赖的投递状态已改变。</summary>
    public static readonly Error Conflict = new("platform.delivery_conflict", "投递状态已经改变，请重新读取。");

    /// <summary>两个存储适配器共用的条件重试规则；保留消息身份，重新开放失败预算。</summary>
    /// <param name="entry">已锁定的当前投递。</param>
    /// <param name="expectedDeadLetteredAt">操作者观察到的停止时刻。</param>
    /// <returns>恢复后的待投递项，或状态冲突。</returns>
    public static Result<OutboxEntry> Retry(OutboxEntry? entry, DateTimeOffset expectedDeadLetteredAt) =>
        entry?.EventName == SettingCommittedV1.Name && entry.RetryDelivery(expectedDeadLetteredAt) is { } retried
            ? Result.Success(retried) : Result.Failure<OutboxEntry>(Conflict);

    /// <summary>从内部 Outbox 裁剪可公开的状态。</summary>
    /// <param name="entry">所属上下文消息。</param>
    /// <returns>最小状态。</returns>
    public static SettingAuditDelivery From(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Id, entry.IsDelivered ? "Delivered" : entry.IsDeadLettered ? "DeadLettered" : "Pending",
            entry.AttemptCount, entry.NextAttemptAt, entry.DeadLetteredAt);
    }
}

/// <summary>设置审计投递的有界调查与条件重试接口。</summary>
public interface ISettingAuditDelivery
{
    /// <summary>列出指定状态中最早的消息，最多 100 条。</summary>
    /// <param name="state">Pending / Delivered / DeadLettered。</param>
    /// <param name="limit">1 到 100。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>投递状态。</returns>
    Task<IReadOnlyList<SettingAuditDelivery>> ListAsync(string state, int limit, CancellationToken cancellationToken = default);

    /// <summary>仅在停止重试的状态仍匹配时恢复投递；保持原消息身份。</summary>
    /// <param name="messageId">消息。</param>
    /// <param name="expectedDeadLetteredAt">调用方看到的停止时刻。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>已恢复，或状态已改变。</returns>
    Task<Result<SettingAuditDelivery>> RetryAsync(Guid messageId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default);
}
