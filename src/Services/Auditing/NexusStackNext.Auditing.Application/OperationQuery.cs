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
    /// <summary>验证分页、固定结果、字符串长度与时间边界。</summary>
    /// <returns>有效或过滤条件错误。</returns>
    public Result Validate() => Page is >= 1 and <= 1000 && Limit is >= 1 and <= 100
        && Optional(Source, 64) && Optional(ActorId, 200) && Optional(TraceId, 128) && OperationId != Guid.Empty
        && (Outcome is null or "unconfirmed" or "completed" or "accepted" or "rejected" or "failed" or "canceled")
        && (From is null || To is null || From <= To)
        ? Result.Success() : Result.Failure(new Error("auditing.operations.query_invalid", "操作查询的分页、过滤条件或时间范围无效。"));

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
            finished?.Outcome ?? "unconfirmed", finished?.StatusCode, finished?.DurationMs);
    }
}

/// <summary>操作级调查结果页。</summary>
/// <param name="Operations">本页执行。</param>
/// <param name="Total">过滤后的执行总数。</param>
public sealed record OperationPage(IReadOnlyList<OperationSummary> Operations, long Total);
