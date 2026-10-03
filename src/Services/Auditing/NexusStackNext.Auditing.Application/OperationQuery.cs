using System.Linq.Expressions;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>按最终已观察阶段过滤执行；时间过滤使用该阶段的来源发生时刻。</summary>
/// <param name="Page">1 到 1000。</param>
/// <param name="Limit">1 到 100。</param>
/// <param name="Source">来源精确匹配。</param>
/// <param name="OperationId">执行标识。</param>
/// <param name="Outcome">结果；unconfirmed 代表尚无完成证据。</param>
/// <param name="ActorId">执行者精确匹配。</param>
/// <param name="TraceId">追踪标识精确匹配。</param>
/// <param name="From">来源时刻下界，含边界。</param>
/// <param name="To">来源时刻上界，含边界。</param>
public sealed record OperationQuery(int Page, int Limit, string? Source = null, Guid? OperationId = null,
    string? Outcome = null, string? ActorId = null, string? TraceId = null, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    /// <summary>动作精确匹配。</summary>
    public string? Action { get; init; }
    /// <summary>客体类型精确匹配。</summary>
    public string? SubjectType { get; init; }
    /// <summary>安全客体标识精确匹配。</summary>
    public string? SubjectId { get; init; }
    /// <summary>调用关联。</summary>
    public string? CorrelationId { get; init; }
    /// <summary>原发起人，与当前 Actor 分开。</summary>
    public string? InitiatorId { get; init; }
    /// <summary>根操作。</summary>
    public Guid? RootOperationId { get; init; }
    /// <summary>根操作来源。</summary>
    public string? RootSource { get; init; }
    /// <summary>直接上游操作。</summary>
    public Guid? ParentOperationId { get; init; }
    /// <summary>直接上游来源。</summary>
    public string? ParentSource { get; init; }
    /// <summary>持久任务标识。</summary>
    public Guid? TaskId { get; init; }
    /// <summary>任务租约轮次；必须同时提供任务标识。</summary>
    public long? TaskEpoch { get; init; }

    /// <summary>省略时间时查询最近七天；单边界按七天补齐，并统一为 UTC。</summary>
    /// <param name="now">调用方提供的当前时刻。</param>
    /// <returns>具有明确时间边界的查询。</returns>
    public OperationQuery Normalize(DateTimeOffset now)
    {
        var window = InvestigationWindow.Resolve(From, To, now);
        return this with { From = window.From, To = window.To };
    }

    /// <summary>验证分页、固定结果、字符串长度与时间边界。</summary>
    /// <returns>有效或过滤条件错误。</returns>
    public Result Validate() => Page is >= 1 and <= 1000 && Limit is >= 1 and <= 100
        && Optional(Source, 64) && Optional(ActorId, 200) && Optional(TraceId, 128) && OperationId != Guid.Empty
        && Optional(Action, 200) && Optional(SubjectType, 100) && Optional(SubjectId, 36)
        && Optional(CorrelationId, 64) && Optional(InitiatorId, 200) && Optional(RootSource, 64) && Optional(ParentSource, 64)
        && RootOperationId != Guid.Empty && ParentOperationId != Guid.Empty && TaskId != Guid.Empty
        && (TaskEpoch is null || (TaskEpoch > 0 && TaskId is not null))
        && (Outcome is null or "unconfirmed" or "completed" or "accepted" or "rejected" or "failed" or "canceled" or "superseded" or "lease_lost" or "skipped" or "deferred" or "duplicate")
        && InvestigationWindow.IsValid(From, To)
        ? Result.Success() : Result.Failure(new Error("auditing.operations.query_invalid", "操作查询的分页、过滤条件或时间范围无效，时间窗口最多三十一天。"));

    /// <summary>筛选最终已有证据，必须在阶段合并之后使用，以免补造 unconfirmed。</summary>
    /// <returns>可由两个适配器一致执行的筛选谓词。</returns>
    public Expression<Func<OperationObservation, bool>> Predicate()
    {
        var operation = new OperationId(OperationId ?? Guid.Empty);
        return item => item.Data.OccurredAt >= From && item.Data.OccurredAt <= To
            && (Source == null || item.Data.Source == Source)
            && (OperationId == null || item.Data.OperationId == operation)
            && (ActorId == null || item.Data.ActorId == ActorId)
            && (TraceId == null || item.Data.TraceId == TraceId)
            && (Outcome == null || (Outcome == "unconfirmed" ? item.Data.Phase == "started" : item.Data.Outcome == Outcome))
            && (Action == null || item.Data.Metadata != null && item.Data.Metadata.Action == Action)
            && (SubjectType == null || item.Data.Metadata != null && item.Data.Metadata.SubjectType == SubjectType)
            && (SubjectId == null || item.Data.Metadata != null && item.Data.Metadata.SubjectId == SubjectId)
            && (CorrelationId == null || item.Data.Metadata != null && item.Data.Metadata.CorrelationId == CorrelationId)
            && (InitiatorId == null || item.Data.Metadata != null && item.Data.Metadata.InitiatorId == InitiatorId)
            && (RootOperationId == null || item.Data.Metadata != null && item.Data.Metadata.RootOperationId == RootOperationId)
            && (RootSource == null || item.Data.Metadata != null && item.Data.Metadata.RootSource == RootSource)
            && (ParentOperationId == null || item.Data.Metadata != null && item.Data.Metadata.ParentOperationId == ParentOperationId)
            && (ParentSource == null || item.Data.Metadata != null && item.Data.Metadata.ParentSource == ParentSource)
            && (TaskId == null || item.Data.Metadata != null && item.Data.Metadata.TaskId == TaskId)
            && (TaskEpoch == null || item.Data.Metadata != null && item.Data.Metadata.TaskEpoch == TaskEpoch);
    }

    private static bool Optional(string? value, int maximum) => value is null
        || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl));
}

/// <summary>一次执行的已有证据；完成先到也能独立呈现。</summary>
/// <param name="OperationId">执行标识。</param>
/// <param name="Source">来源。</param>
/// <param name="Kind">入口种类。</param>
/// <param name="TraceId">追踪标识。</param>
/// <param name="ActorId">最终观察阶段的执行者。</param>
/// <param name="HttpMethod">HTTP 方法。</param>
/// <param name="RouteTemplate">路由模板。</param>
/// <param name="StartedAt">已收到的开始时刻。</param>
/// <param name="FinishedAt">已收到的完成时刻。</param>
/// <param name="Outcome">执行观察结果；没有完成记录时为 unconfirmed。</param>
/// <param name="StatusCode">已观察的响应状态。</param>
/// <param name="DurationMs">来源测得耗时。</param>
public sealed record OperationSummary(Guid OperationId, string Source, string Kind, string TraceId, string? ActorId,
    string? HttpMethod, string? RouteTemplate, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string Outcome, int? StatusCode, long? DurationMs)
{
    /// <summary>最终已观察阶段的安全元数据；旧记录可能为空。</summary>
    public OperationMetadata? Metadata { get; init; }

    /// <summary>从同一次执行的不可变观察汇总。</summary>
    /// <param name="observations">相同来源与执行的阶段。</param>
    /// <returns>只陈述已有证据的结果。</returns>
    public static OperationSummary From(IEnumerable<OperationObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var phases = observations.ToArray();
        var started = phases.SingleOrDefault(item => item.Data.Phase == "started")?.Data;
        var finished = phases.SingleOrDefault(item => item.Data.Phase == "finished")?.Data;
        var latest = finished ?? started ?? throw new ArgumentException("至少需要一条阶段观察。", nameof(observations));
        return new OperationSummary(latest.OperationId.Value, latest.Source, latest.Kind, latest.TraceId, latest.ActorId,
            latest.HttpMethod, latest.RouteTemplate, started?.OccurredAt, finished?.OccurredAt,
            finished?.Outcome ?? "unconfirmed", finished?.StatusCode, finished?.DurationMs)
        { Metadata = latest.Metadata };
    }
}

/// <summary>操作级调查结果页。</summary>
/// <param name="Operations">本页执行。</param>
/// <param name="Total">过滤后的执行总数。</param>
public sealed record OperationPage(IReadOnlyList<OperationSummary> Operations, long Total);
