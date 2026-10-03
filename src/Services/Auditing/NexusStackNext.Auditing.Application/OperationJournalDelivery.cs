using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>来源交付状态的安全视图，不包含消息载荷或原始异常。</summary>
/// <param name="MessageId">稳定消息身份。</param>
/// <param name="Source">观察来源。</param>
/// <param name="OperationId">操作身份。</param>
/// <param name="Phase">观察阶段。</param>
/// <param name="State">Pending、Delivered 或 DeadLettered。</param>
/// <param name="AttemptCount">当前投递预算内尝试次数。</param>
/// <param name="NextAttemptAt">下次自动尝试时刻。</param>
/// <param name="DeadLetteredAt">停止投递的时刻。</param>
/// <param name="DeliveredAt">首次发布确认时刻，不代表中央接纳。</param>
/// <param name="RetryRevision">显式恢复次数，用于拒绝过期管理操作。</param>
/// <param name="FailureCode">固定安全故障分类。</param>
public sealed record OperationJournalDelivery(Guid MessageId, string Source, Guid OperationId, string Phase, string State,
    int AttemptCount, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeadLetteredAt, DateTimeOffset? DeliveredAt,
    long RetryRevision, string? FailureCode);

/// <summary>死信分页参数；内容随交付变化，重试仍须校验读取到的状态。</summary>
/// <param name="Page">页码，1 至 1000。</param>
/// <param name="Limit">每页数量，1 至 100。</param>
/// <param name="Source">可选的来源精确过滤。</param>
public sealed record OperationJournalDeadLetterQuery(int Page = 1, int Limit = 50, string? Source = null)
{
    /// <summary>验证有界分页与来源，防止无界读取。</summary>
    /// <returns>有效或固定错误。</returns>
    public Result Validate() => Page is >= 1 and <= 1000 && Limit is >= 1 and <= 100
        && (Source is null || (Source.Length is >= 1 and <= 64 && !Source.Any(char.IsControl)))
        ? Result.Success() : Result.Failure(OperationJournalDeliveryErrors.InvalidQuery);
}

/// <summary>不含载荷的有界死信页。</summary>
/// <param name="Page">当前页。</param>
/// <param name="Limit">页大小。</param>
/// <param name="Total">查询时匹配的数量；并发变化时可能与本页内容不同步，也不承诺跨页快照。</param>
/// <param name="Items">本页安全视图。</param>
public sealed record OperationJournalDeadLetterPage(int Page, int Limit, long Total, IReadOnlyList<OperationJournalDelivery> Items);

/// <summary>来源交付管理的稳定错误。</summary>
public static class OperationJournalDeliveryErrors
{
    /// <summary>查询参数无效。</summary>
    public static readonly Error InvalidQuery = new("operation_journal.query_invalid", "日志交付查询参数无效。");
    /// <summary>来源记录不存在或已按保留策略清理。</summary>
    public static readonly Error NotFound = new("operation_journal.delivery_not_found", "来源交付记录不存在或已清理。");
    /// <summary>投递状态已变化，必须重新读取后决定。</summary>
    public static readonly Error Conflict = new("operation_journal.delivery_conflict", "投递状态已变化，不能应用过期恢复操作。");
}
