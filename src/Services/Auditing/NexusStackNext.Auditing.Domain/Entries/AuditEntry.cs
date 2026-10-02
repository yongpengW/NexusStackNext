using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Domain.Entries;

/// <summary>审计条目标识。</summary>
public sealed record AuditEntryId : StronglyTypedId<long>
{
    /// <summary>由调用方提供标识。</summary>
    /// <param name="value">标识。</param>
    public AuditEntryId(long value) : base(value) { }
}

/// <summary>来源服务已经提交的最小事实，不包含请求体或业务字段值。</summary>
/// <param name="MessageId">稳定消息标识。</param>
/// <param name="EventName">版本化来源事件。</param>
/// <param name="Source">来源上下文。</param>
/// <param name="Action">已完成动作。</param>
/// <param name="SubjectType">客体类型。</param>
/// <param name="SubjectId">客体标识。</param>
/// <param name="SubjectVersion">提交后的客体版本。</param>
/// <param name="ActorId">来源服务认证的发起者；系统动作为空。</param>
/// <param name="OccurredAt">来源服务记录的发生时刻。</param>
/// <param name="TraceId">来源执行追踪标识；不是授权证据。</param>
/// <param name="CorrelationId">来源执行关联标识；不是授权证据。</param>
public sealed record AuditFact(Guid MessageId, string EventName, string Source, string Action, string SubjectType,
    string SubjectId, long SubjectVersion, string? ActorId, DateTimeOffset OccurredAt, string TraceId, string CorrelationId);

/// <summary>追加且不可修改的审计事实及接收时刻。</summary>
public sealed class AuditEntry : AggregateRoot<AuditEntryId>
{
    private AuditEntry(AuditEntryId id) : base(id) { Fact = null!; }

    /// <summary>不可变的来源事实。</summary>
    public AuditFact Fact { get; private init; }

    /// <summary>本上下文接收并登记的时刻。</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>验证并记录已提交事实。</summary>
    /// <param name="id">条目标识。</param>
    /// <param name="fact">来源事实。</param>
    /// <param name="recordedAt">接收时刻。</param>
    /// <returns>合法的不可变记录，或校验失败。</returns>
    public static Result<AuditEntry> Record(AuditEntryId id, AuditFact fact, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (string.IsNullOrWhiteSpace(fact.Action))
        {
            return Result.Failure<AuditEntry>(new Error("auditing.action.empty", "审计动作不能为空。"));
        }
        if (string.IsNullOrWhiteSpace(fact.SubjectType) || string.IsNullOrWhiteSpace(fact.SubjectId))
        {
            return Result.Failure<AuditEntry>(new Error("auditing.subject.empty", "审计客体类型与标识都不能为空。"));
        }
        if (fact.MessageId == Guid.Empty || fact.SubjectVersion < 1 || fact.OccurredAt == default || fact.OccurredAt.Offset != TimeSpan.Zero
            || !Valid(fact.EventName, 200) || !Valid(fact.Source, 64) || !Valid(fact.Action, 200)
            || !Valid(fact.SubjectType, 100) || !Valid(fact.SubjectId, 200)
            || (fact.ActorId is not null && !Valid(fact.ActorId, 200))
            || !Valid(fact.TraceId, 128) || !Valid(fact.CorrelationId, 128))
        {
            return Result.Failure<AuditEntry>(new Error("auditing.fact.invalid", "审计事实缺少有效来源、身份或关联信息。"));
        }
        return Result.Success(new AuditEntry(id) { Fact = fact, RecordedAt = recordedAt });
    }

    private static bool Valid(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
}
