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
/// <param name="ActorId">来源服务认证的当前执行者；系统动作为空。</param>
/// <param name="OccurredAt">来源服务记录的发生时刻。</param>
/// <param name="TraceId">来源执行追踪标识；不是授权证据。</param>
/// <param name="CorrelationId">来源执行关联标识；不是授权证据。</param>
public sealed record AuditFact(Guid MessageId, string EventName, string Source, string Action, string SubjectType,
    string SubjectId, long SubjectVersion, string? ActorId, DateTimeOffset OccurredAt, string TraceId, string CorrelationId)
{
    /// <summary>提交发生在哪次执行中；缺少操作观察不否定已提交事实。</summary>
    public AuditExecution? Execution { get; init; }
    /// <summary>发生时有明确业务关系的另一客体；不从当前业务状态补造。</summary>
    public AuditSubjectReference? RelatedSubject { get; init; }
    /// <summary>容量治理事实的明确前后数值；普通业务事实为空。</summary>
    public AuditCapacityPolicyChange? CapacityPolicyChange { get; init; }
}

/// <summary>由上下文、类型与内部标识共同确定的关联客体。</summary>
/// <param name="Context">拥有该客体的上下文。</param>
/// <param name="Type">该上下文中的客体类型。</param>
/// <param name="Id">内部标识。</param>
public sealed record AuditSubjectReference(string Context, string Type, string Id);

/// <summary>事实所属执行与原发起关系；来源业务上下文与执行宿主可以不同。</summary>
/// <param name="OperationId">当前执行标识。</param>
/// <param name="Source">当前执行的来源。</param>
/// <param name="RootOperationId">根操作标识。</param>
/// <param name="RootSource">根操作来源。</param>
/// <param name="InitiatorId">原发起人，不代替事实的当前 Actor。</param>
public sealed record AuditExecution(Guid OperationId, string Source, Guid RootOperationId, string RootSource, string? InitiatorId);

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
            || !Valid(fact.TraceId, 128) || !Valid(fact.CorrelationId, 128)
            || (fact.CapacityPolicyChange is null && (fact.SubjectType == "fact-capacity-policy"
                || fact.EventName == fact.Source + ".fact-capacity-policy-changed.v1"))
            || (fact.CapacityPolicyChange is { } policy && !ValidPolicy(fact, policy))
            || (fact.RelatedSubject is { } related && (!Valid(related.Context, 64) || !Valid(related.Type, 100) || !Valid(related.Id, 200)))
            || (fact.Execution is { } execution && (execution.OperationId == Guid.Empty || execution.RootOperationId == Guid.Empty
                || !Valid(execution.Source, 64) || !Valid(execution.RootSource, 64)
                || (execution.InitiatorId is not null && !Valid(execution.InitiatorId, 200)))))
        {
            return Result.Failure<AuditEntry>(new Error("auditing.fact.invalid", "审计事实缺少有效来源、身份或关联信息。"));
        }
        return Result.Success(new AuditEntry(id) { Fact = fact, RecordedAt = recordedAt });
    }

    private static bool Valid(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool ValidPolicy(AuditFact fact, AuditCapacityPolicyChange policy) => policy.RequestId != Guid.Empty
        && policy.PolicyRevision > 1 && policy.PolicyRevision == fact.SubjectVersion && policy.Reason == "operator-adjustment"
        && fact.SubjectType == "fact-capacity-policy" && fact.SubjectId == fact.Source && Valid(fact.ActorId, 200)
        && fact.Action == fact.Source + ".fact-capacity-policy.changed"
        && fact.EventName == fact.Source + ".fact-capacity-policy-changed.v1"
        && ValidLimits(policy.Previous) && ValidLimits(policy.Current) && policy.Previous != policy.Current;

    private static bool ValidLimits(AuditCapacityPolicyLimits? limits) => limits is { MaxRecords: > 0, MaxPayloadBytes: > 0, MaxRecordPayloadBytes: > 0 }
        && limits.MaxRecordPayloadBytes <= limits.MaxPayloadBytes;
}
