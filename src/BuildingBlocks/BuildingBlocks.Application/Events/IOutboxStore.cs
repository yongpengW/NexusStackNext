namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// Outbox 存储。由 EF Core 实现（票据 19），与聚合改动同一事务写入。
/// <para>
/// 刻意只有四个方法：读待投递、标记成功、标记失败、标记死信。
/// <b>没有 <c>Add</c></b>——写入是聚合持久化的一部分（拦截器在 <c>SaveChanges</c> 时收集领域事件并落 Outbox），
/// 不是投递器的职责；给投递器一个"写"的口子只会让"同事务"这条保证变得可疑。
/// </para>
/// </summary>
public interface IOutboxStore
{
    /// <summary>读取可投递的记录：未投递、未死信，且到达下次尝试时间。</summary>
    /// <param name="batchSize">单次最多读取多少条。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待投递记录，按创建顺序。</returns>
    Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>标记投递成功。</summary>
    /// <param name="id">记录标识。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>记录一次失败并安排下次尝试。</summary>
    /// <param name="id">记录标识。</param>
    /// <param name="failure">失败原因。</param>
    /// <param name="nextAttemptAt">下次尝试时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task MarkFailedAsync(
        Guid id,
        string failure,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    /// <summary>标记进入死信，不再重试。</summary>
    /// <param name="id">记录标识。</param>
    /// <param name="failure">最终失败原因。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task MarkDeadLetteredAsync(
        Guid id,
        string failure,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
