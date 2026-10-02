using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>审计存储：同一次提交登记消息身份与不可变记录。</summary>
public interface IAuditEntryStore
{
    /// <summary>原子接纳事实。相同身份相同内容幂等，不同内容拒绝。</summary>
    /// <param name="entry">已校验事实。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>首次、重复或身份冲突。</returns>
    Task<Result<IngestionOutcome>> AcceptAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按记录顺序倒序读取有界调查页。</summary>
    /// <param name="page">1 到 1000 的页码。</param>
    /// <param name="limit">1 到 100 的页大小。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>本页和总数。</returns>
    Task<AuditPage> QueryAsync(int page, int limit, CancellationToken cancellationToken = default);
}

/// <summary>受限调查的一页审计事实。</summary>
/// <param name="Entries">本页事实。</param>
/// <param name="Total">总条数。</param>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, long Total);

/// <summary>一次摄取的结果。</summary>
public enum IngestionOutcome
{
    /// <summary>首次收到，已记录。</summary>
    Accepted,
    /// <summary>此前已接纳相同事实，没有再次记录。</summary>
    Duplicate,
}

/// <summary>校验事实后原子接纳；失败不会占用去重身份。</summary>
/// <param name="entries">事务性存储。</param>
/// <param name="ids">条目标识。</param>
/// <param name="clock">接收时刻。</param>
public sealed class AuditIngestion(IAuditEntryStore entries, IIdGenerator ids, IClock clock)
{
    /// <summary>审计消费端身份。</summary>
    public const string ConsumerName = "auditing.entries";
    /// <summary>相同消息身份不允许对应另一项事实。</summary>
    public static readonly Error MessageConflict = new("auditing.message_conflict", "消息身份已对应不同的审计事实。");

    /// <summary>摄取来源服务已经提交的事实。</summary>
    /// <param name="fact">事实。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>首次、重复或校验/身份冲突。</returns>
    public Task<Result<IngestionOutcome>> IngestAsync(AuditFact fact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var entry = AuditEntry.Record(new AuditEntryId(ids.NextId()), fact, clock.UtcNow);
        return entry.IsSuccess ? entries.AcceptAsync(entry.Value, cancellationToken)
            : Task.FromResult(Result.Failure<IngestionOutcome>(entry.Error));
    }
}
