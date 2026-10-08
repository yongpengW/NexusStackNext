using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Costing.Application;

/// <summary>一次不可变成本批次；受理不修改成本对象。</summary>
/// <param name="BatchRequestId">调用方保留的幂等身份，同时为批次标识。</param>
/// <param name="Rows">有序原始行，不能在去重前丢弃错误行。</param>
[BackgroundWorkAcceptance]
public sealed record AcceptCostBatch(Guid BatchRequestId, IReadOnlyList<CostBatchInput> Rows) : ICommand<CostBatchStatus>;

/// <summary>一个原始成本输入。</summary>
/// <param name="SourceRow">用户原始行位置；缺省时使用逻辑序号。</param>
/// <param name="ItemId">成本对象。</param>
/// <param name="ExpectedVersion">创建为零，更新为观察到的版本。</param>
/// <param name="PurchaseCost">单位采购成本。</param>
/// <param name="FreightCost">单位运费。</param>
public sealed record CostBatchInput(int SourceRow, Guid ItemId, long ExpectedVersion, decimal PurchaseCost, decimal FreightCost);

/// <summary>读取一个持久化批次；不返回输入快照。</summary>
/// <param name="BatchId">批次标识。</param>
public sealed record GetCostBatch(Guid BatchId) : IQuery<CostBatchStatus>;

/// <summary>分页读取原始行的结论与子计算状态。</summary>
/// <param name="BatchId">批次标识。</param>
/// <param name="Page">从一开始的页码。</param>
/// <param name="Limit">每页最多二百项。</param>
public sealed record ListCostBatchRows(Guid BatchId, int Page = 1, int Limit = 50) : IQuery<CostBatchRowPage>;

/// <summary>有界批次列表，不携带输入快照及完整执行历史。</summary>
/// <param name="Page">从一开始的页码。</param>
/// <param name="Limit">每页一至二百项。</param>
/// <param name="State">可选精确状态。</param>
public sealed record ListCostBatches(int Page = 1, int Limit = 50, string? State = null) : IQuery<CostBatchPage>;

/// <summary>同一读取快照内的批次列表。</summary>
/// <param name="Items">本页批次元数据。</param>
/// <param name="Total">匹配总数。</param>
public sealed record CostBatchPage(IReadOnlyList<CostBatchStatus> Items, long Total);

/// <summary>分页读取执行历史，条件重试不会清除旧尝试。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="Page">从一开始的页码。</param>
/// <param name="Limit">每页一至二百项。</param>
public sealed record ListCostBatchAttempts(Guid BatchId, int Page = 1, int Limit = 50) : IQuery<CostBatchAttemptPage>;

/// <summary>有限执行历史页。</summary>
/// <param name="Items">本页尝试。</param>
/// <param name="Total">尝试总数。</param>
public sealed record CostBatchAttemptPage(IReadOnlyList<CostingAttempt> Items, long Total);

/// <summary>批次执行元数据；完成导入不代表子计算或定价完成。</summary>
/// <param name="BatchId">批次标识。</param>
/// <param name="State">当前状态。</param>
/// <param name="CreatedAt">数据库首次接受时刻。</param>
/// <param name="TotalRows">全部原始行数。</param>
/// <param name="Checkpoint">已连续提交到的逻辑序号。</param>
/// <param name="Imported">实际改变成本输入的行数。</param>
/// <param name="Unchanged">未改变输入的行数。</param>
/// <param name="DuplicateSuperseded">末条策略淘汰的行数。</param>
/// <param name="Rejected">业务拒绝的行数。</param>
public sealed record CostBatchStatus(Guid BatchId, string State, DateTimeOffset CreatedAt, int TotalRows,
    int Checkpoint, int Imported, int Unchanged, int DuplicateSuperseded, int Rejected)
{
    /// <summary>尚未得到逐行结论的有效行数；被取消的未执行行保持 Pending。</summary>
    public int Pending => TotalRows - Imported - Unchanged - DuplicateSuperseded - Rejected;
    /// <summary>当前执行代次。</summary>
    public long Epoch { get; init; }
    /// <summary>当前预算内的尝试数。</summary>
    public int Attempts { get; init; }
    /// <summary>本次租约期限。</summary>
    public DateTimeOffset? LeaseUntil { get; init; }
    /// <summary>本次领取的固定总期限。</summary>
    public DateTimeOffset? MaxLeaseUntil { get; init; }
    /// <summary>最近技术故障的稳定错误码。</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>领取一个到期批次。</summary>
[CommandObservationSuppression("批次轮询只协调执行权，实际导入由任务观察记录。")]
public sealed record ClaimCostBatch : ICommand<CostBatchLease?>;

/// <summary>在当前执行权下按配置处理一段；每行独立提交。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="Epoch">当前执行代次。</param>
[CommandObservationSuppression("逐行导入通过后台任务观察采集，不重复生成命令观察。")]
public sealed record ExecuteCostBatchSegment(Guid BatchId, long Epoch) : ICommand<bool>;

/// <summary>记录持有有效租约的技术失败并安排有限重试。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="Epoch">当前执行代次。</param>
[CommandObservationSuppression("仅持久化失败安排，实际故障由后台任务观察记录。")]
public sealed record FailCostBatch(Guid BatchId, long Epoch) : ICommand<bool>;

/// <summary>批次执行权；子计算拥有独立的任务身份及租约。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="Epoch">执行代次。</param>
/// <param name="ExpiresAt">数据库裁决的租约期限。</param>
public sealed record CostBatchLease(Guid BatchId, long Epoch, DateTimeOffset ExpiresAt);

/// <summary>停止尚未提交的导入行，不取消已登记的子计算。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="ExpectedEpoch">观察到的执行代次。</param>
public sealed record CancelCostBatch(Guid BatchId, long ExpectedEpoch) : ICommand<CostBatchStatus>;

/// <summary>条件重新开放失败批次，保留输入、行身份和检查点。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="ExpectedEpoch">观察到的失败代次。</param>
[BackgroundWorkAcceptance]
public sealed record RetryCostBatch(Guid BatchId, long ExpectedEpoch) : ICommand<CostBatchStatus>;

/// <summary>延长仍有效的批次租约，不移动本次领取的总期限。</summary>
/// <param name="BatchId">批次身份。</param>
/// <param name="Epoch">持有的执行代次。</param>
[CommandObservationSuppression("仅协调批次租约，实际导入由后台观察记录。")]
public sealed record RenewCostBatch(Guid BatchId, long Epoch) : ICommand<CostBatchLease>;

/// <summary>Costing 私有批次策略，没有通用 batch 框架。</summary>
public sealed record CostingBatchOptions
{
    /// <summary>每段读取的最多行数。</summary>
    public int SegmentSize { get; init; } = 100;
    /// <summary>单行短事务的总预算；锁及 SQL 的预算必须更短。</summary>
    public TimeSpan RowTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>批次受理包含连接、身份锁与有界快照写入的总预算。</summary>
    public TimeSpan AcceptanceTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>启动时拒绝无界配置。</summary>
    public void Validate()
    {
        if (SegmentSize is < 1 or > 500 || RowTimeout < TimeSpan.FromSeconds(2) || RowTimeout > TimeSpan.FromSeconds(30)
            || AcceptanceTimeout < TimeSpan.FromSeconds(5) || AcceptanceTimeout > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("批次每段须为 1 到 500 行，单行预算须为 2 到 30 秒，受理预算须为 5 到 60 秒。");
        }
    }
}

/// <summary>分页元数据。</summary>
/// <param name="Items">本页原始行结论。</param>
/// <param name="Total">全部原始行数。</param>
public sealed record CostBatchRowPage(IReadOnlyList<CostBatchRowStatus> Items, long Total);

/// <summary>不含成本快照的行结论；Pending 行的 TaskId 仅为保留身份。</summary>
/// <param name="Sequence">从一开始的逻辑序号。</param>
/// <param name="SourceRow">原始输入位置。</param>
/// <param name="ItemId">成本对象。</param>
/// <param name="Outcome">持久处理结论。</param>
/// <param name="EffectiveSequence">末条生效的逻辑序号。</param>
/// <param name="TaskId">保留或已登记的子计算身份，重复行为空。</param>
/// <param name="ErrorCode">稳定业务错误码。</param>
/// <param name="CalculationState">子计算状态；未登记时为空。</param>
public sealed record CostBatchRowStatus(int Sequence, int SourceRow, Guid ItemId, string Outcome,
    int EffectiveSequence, Guid? TaskId, string? ErrorCode, string? CalculationState);
