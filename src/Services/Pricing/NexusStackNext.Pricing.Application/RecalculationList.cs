using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Pricing.Application;

/// <summary>按首次接受时间倒序、未知时间最后、任务 ID 升序列出工作；每次查询是独立快照。</summary>
/// <param name="Page">从 1 开始的页码；偏移不得超过 100000。</param>
/// <param name="Limit">每页 1 到 200 项，默认 50。</param>
/// <param name="State">可选精确执行状态。</param>
/// <param name="ItemId">可选定价对象标识。</param>
public sealed record ListRecalculations(int Page = 1, int Limit = 50, string? State = null, Guid? ItemId = null) : IQuery<RecalculationPage>;

/// <summary>同一次查询快照中的条目与总数。</summary>
/// <param name="Items">有界执行元数据，不含定价输入或历史集合。</param>
/// <param name="Total">符合条件的总数。</param>
public sealed record RecalculationPage(IReadOnlyList<RecalculationSummary> Items, long Total);

/// <summary>定价任务的运维元数据；有效执行权还必须满足 Running、代次和数据库期限条件。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="ItemId">定价对象。</param>
/// <param name="State">执行状态。</param>
/// <param name="CreatedAt">首次接受时刻；升级前未知。</param>
/// <param name="AvailableAt">最早可领取时刻。</param>
/// <param name="Epoch">执行代次。</param>
/// <param name="Attempts">本轮已领取次数。</param>
/// <param name="LeaseUntil">当前或最近租约期限。</param>
/// <param name="MaxLeaseUntil">该次领取的固定总期限；旧记录可能未知。</param>
/// <param name="ErrorCode">最近失败的稳定错误码。</param>
public sealed record RecalculationSummary(Guid TaskId, Guid ItemId, string State, DateTimeOffset? CreatedAt,
    DateTimeOffset AvailableAt, long Epoch, int Attempts, DateTimeOffset? LeaseUntil, DateTimeOffset? MaxLeaseUntil, string? ErrorCode);
