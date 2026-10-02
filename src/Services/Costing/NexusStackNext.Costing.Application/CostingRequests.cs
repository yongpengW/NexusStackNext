using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Costing.Application;

/// <summary>更新演示成本核算输入并登记重算；RequestId 是调用方在重试间保留的业务请求标识。</summary>
/// <param name="RequestId">幂等请求标识，同时作为任务标识。</param>
/// <param name="ItemId">成本核算对象标识。</param>
/// <param name="ExpectedVersion">预期聚合版本；创建时为零。</param>
/// <param name="PurchaseCost">单位成本。</param>
/// <param name="FreightCost">分摊到单位的运费。</param>
public sealed record UpdateCostInputs(Guid RequestId, Guid ItemId, long ExpectedVersion, decimal PurchaseCost, decimal FreightCost)
    : ICommand<CostCalculationStatus>;

/// <summary>读取任务与最近执行结果。</summary>
/// <param name="TaskId">登记时返回的任务标识。</param>
public sealed record GetCostCalculation(Guid TaskId) : IQuery<CostCalculationStatus>;

/// <summary>可查询的持久化工作状态。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="ItemId">成本核算对象。</param>
/// <param name="State">执行状态。</param>
/// <param name="InputRevision">待处理的输入版本。</param>
public sealed record CostCalculationStatus(Guid TaskId, Guid ItemId, string State, long InputRevision)
{
    /// <summary>每次成功领取递增；人工重试也不清零。</summary>
    public long Epoch { get; init; }
    /// <summary>当前自动重试预算内已经领取的次数。</summary>
    public int Attempts { get; init; }
    /// <summary>最近失败的稳定错误码。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>下一次可领取的时间。</summary>
    public DateTimeOffset AvailableAt { get; init; }
    /// <summary>每次领取的历史。</summary>
    public IReadOnlyList<CostingAttempt> History { get; init; } = [];
}

/// <summary>一次领取的可观察历史。</summary>
/// <param name="Epoch">领取代次。</param>
/// <param name="StartedAt">领取时刻。</param>
/// <param name="FinishedAt">结束时刻。</param>
/// <param name="Outcome">执行结论。</param>
/// <param name="ErrorCode">稳定错误码，不含异常正文。</param>
public sealed record CostingAttempt(long Epoch, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Outcome, string? ErrorCode);

/// <summary>工作进程报告本次计算失败；旧执行代次不能修改当前状态。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Epoch">执行代次。</param>
public sealed record FailCostingWork(Guid TaskId, long Epoch) : ICommand<bool>;

/// <summary>授权操作者重新启用一个失败任务，ExpectedEpoch 防止重复操作。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="ExpectedEpoch">操作者看到的失败代次。</param>
public sealed record RetryCostingWork(Guid TaskId, long ExpectedEpoch) : ICommand<CostCalculationStatus>;

/// <summary>工作进程领取一项到期工作；没有可领取工作时返回 null。</summary>
public sealed record ClaimCostingWork : ICommand<CostingWorkLease?>;

/// <summary>完成当前执行代次；返回 false 表示执行权已经丢失。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Epoch">领取代次。</param>
public sealed record CompleteCostingWork(Guid TaskId, long Epoch) : ICommand<bool>;

/// <summary>当前工作进程持有的有限执行权。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Epoch">领取代次。</param>
/// <param name="ExpiresAt">数据库给出的到期时刻。</param>
public sealed record CostingWorkLease(Guid TaskId, long Epoch, DateTimeOffset ExpiresAt);

/// <summary>读取成本核算输入与最近派生结果。</summary>
/// <param name="ItemId">成本核算对象标识。</param>
public sealed record GetCostSheet(Guid ItemId) : IQuery<CostSheetView>;

/// <summary>成本核算输入及其结果版本。</summary>
/// <param name="ItemId">对象标识。</param>
/// <param name="Version">乐观并发版本。</param>
/// <param name="PurchaseCost">当前成本。</param>
/// <param name="FreightCost">当前单位运费。</param>
/// <param name="InputRevision">当前输入版本。</param>
/// <param name="CalculatedRevision">已计算版本。</param>
/// <param name="UnitCost">最近演示单位成本。</param>
public sealed record CostSheetView(Guid ItemId, long Version, decimal PurchaseCost, decimal FreightCost,
    long InputRevision, long CalculatedRevision, decimal? UnitCost);
