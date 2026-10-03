using System.Linq.Expressions;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>只调查已提交事实。时间使用来源发生时刻，所有文本条件精确匹配。</summary>
/// <param name="Page">1 到 1000。</param>
/// <param name="Limit">1 到 100。</param>
public sealed record AuditQuery(int Page, int Limit)
{
    /// <summary>来源业务上下文。</summary>
    public string? Source { get; init; }
    /// <summary>已提交动作。</summary>
    public string? Action { get; init; }
    /// <summary>客体类型。</summary>
    public string? SubjectType { get; init; }
    /// <summary>客体标识。</summary>
    public string? SubjectId { get; init; }
    /// <summary>关联客体所属的上下文，与类型和标识一同提供。</summary>
    public string? RelatedContext { get; init; }
    /// <summary>关联客体类型。</summary>
    public string? RelatedSubjectType { get; init; }
    /// <summary>关联客体内部标识。</summary>
    public string? RelatedSubjectId { get; init; }
    /// <summary>实际执行者。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源追踪标识。</summary>
    public string? TraceId { get; init; }
    /// <summary>调用关联标识。</summary>
    public string? CorrelationId { get; init; }
    /// <summary>当前操作。</summary>
    public Guid? OperationId { get; init; }
    /// <summary>当前操作的来源宿主。</summary>
    public string? OperationSource { get; init; }
    /// <summary>根操作。</summary>
    public Guid? RootOperationId { get; init; }
    /// <summary>根操作来源。</summary>
    public string? RootSource { get; init; }
    /// <summary>原发起人。</summary>
    public string? InitiatorId { get; init; }
    /// <summary>来源发生时刻下界，含边界。</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>来源发生时刻上界，含边界。</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>省略时间时查询最近七天；单边界按七天补齐，并统一为 UTC。</summary>
    /// <param name="now">调用方提供的当前时刻。</param>
    /// <returns>具有明确时间边界的查询。</returns>
    public AuditQuery Normalize(DateTimeOffset now)
    {
        var window = InvestigationWindow.Resolve(From, To, now);
        return this with { From = window.From, To = window.To };
    }

    /// <summary>验证精确过滤与至多三十一天的时间窗口；先 Normalize 再验证。</summary>
    /// <returns>可执行或有界参数错误。</returns>
    public Result Validate() => Page is >= 1 and <= 1000 && Limit is >= 1 and <= 100
        && Optional(Source, 64) && Optional(Action, 200) && Optional(SubjectType, 100) && Optional(SubjectId, 200)
        && Optional(RelatedContext, 64) && Optional(RelatedSubjectType, 100) && Optional(RelatedSubjectId, 200)
        && ((RelatedContext is null && RelatedSubjectType is null && RelatedSubjectId is null)
            || (RelatedContext is not null && RelatedSubjectType is not null && RelatedSubjectId is not null))
        && Optional(ActorId, 200) && Optional(TraceId, 128) && Optional(CorrelationId, 128)
        && Optional(OperationSource, 64) && Optional(RootSource, 64) && Optional(InitiatorId, 200)
        && OperationId != Guid.Empty && RootOperationId != Guid.Empty && InvestigationWindow.IsValid(From, To)
        ? Result.Success() : Result.Failure(new Error("auditing.entries.query_invalid", "事实查询的分页、过滤条件或时间范围无效，时间窗口最多三十一天。"));

    /// <summary>供持久及内存适配器共用的筛选语义，不包含跨上下文访问。</summary>
    /// <returns>可翻译为存储查询的精确谓词。</returns>
    public Expression<Func<AuditEntry, bool>> Predicate() => entry =>
        entry.Fact.OccurredAt >= From && entry.Fact.OccurredAt <= To
        && (Source == null || entry.Fact.Source == Source)
        && (Action == null || entry.Fact.Action == Action)
        && (SubjectType == null || entry.Fact.SubjectType == SubjectType)
        && (SubjectId == null || entry.Fact.SubjectId == SubjectId)
        && (RelatedContext == null || entry.Fact.RelatedSubject != null && entry.Fact.RelatedSubject.Context == RelatedContext
            && entry.Fact.RelatedSubject.Type == RelatedSubjectType && entry.Fact.RelatedSubject.Id == RelatedSubjectId)
        && (ActorId == null || entry.Fact.ActorId == ActorId)
        && (TraceId == null || entry.Fact.TraceId == TraceId)
        && (CorrelationId == null || entry.Fact.CorrelationId == CorrelationId)
        && (OperationId == null || entry.Fact.Execution != null && entry.Fact.Execution.OperationId == OperationId)
        && (OperationSource == null || entry.Fact.Execution != null && entry.Fact.Execution.Source == OperationSource)
        && (RootOperationId == null || entry.Fact.Execution != null && entry.Fact.Execution.RootOperationId == RootOperationId)
        && (RootSource == null || entry.Fact.Execution != null && entry.Fact.Execution.RootSource == RootSource)
        && (InitiatorId == null || entry.Fact.Execution != null && entry.Fact.Execution.InitiatorId == InitiatorId);

    private static bool Optional(string? value, int maximum) => value is null
        || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl));
}

internal static class InvestigationWindow
{
    private static readonly long DefaultTicks = TimeSpan.FromDays(7).Ticks;

    public static (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var start = from?.ToUniversalTime() ?? new DateTimeOffset(Math.Max(0, (to ?? now).UtcTicks - DefaultTicks), TimeSpan.Zero);
        var end = to?.ToUniversalTime() ?? (from is null ? now.ToUniversalTime()
            : new DateTimeOffset(Math.Min(DateTimeOffset.MaxValue.UtcTicks, start.UtcTicks + DefaultTicks), TimeSpan.Zero));
        return (start, end);
    }

    public static bool IsValid(DateTimeOffset? from, DateTimeOffset? to) => from is not null && to is not null
        && from <= to && to - from <= TimeSpan.FromDays(31);
}
