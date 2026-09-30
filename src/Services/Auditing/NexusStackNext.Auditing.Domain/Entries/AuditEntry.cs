using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Domain.Entries;

/// <summary>审计条目标识。</summary>
public sealed record AuditEntryId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public AuditEntryId(long value)
        : base(value)
    {
    }
}

/// <summary>
/// 审计条目。<b>只写不改</b>。
/// <para>
/// 这个类里<b>没有任何 setter，也没有任何修改方法</b>。审计的意义就在于"发生过的事不可被改写"，
/// 而"不可改写"如果只靠约定，迟早会有人在某处加一个 <c>Update</c>。这里让它在类型上就不可能——
/// 有一条测试专门用反射断言 `AuditEntry` 不存在公开 setter。
/// </para>
/// <para>
/// 参照仓库的审计存在两个问题：批量操作绕过 <c>SaveChangesInterceptor</c> 导致漏记（ADR-0008），
/// 以及审计写入与业务写入不同事务——业务回滚了，审计却留下了"发生过"的假记录。
/// 新项目里审计由 Outbox 事件驱动，与业务写入同事务。
/// </para>
/// </summary>
public sealed class AuditEntry : AggregateRoot<AuditEntryId>
{
    private AuditEntry(
        AuditEntryId id,
        string action,
        string subjectType,
        string subjectId,
        string? actorId,
        string? detail,
        DateTimeOffset recordedAt)
        : base(id)
    {
        Action = action;
        SubjectType = subjectType;
        SubjectId = subjectId;
        ActorId = actorId;
        Detail = detail;
        RecordedAt = recordedAt;
    }

    /// <summary>发生了什么，例如 <c>identity.user-registered</c>。</summary>
    public string Action { get; }

    /// <summary>被操作的客体类型。</summary>
    public string SubjectType { get; }

    /// <summary>被操作的客体标识。</summary>
    public string SubjectId { get; }

    /// <summary>操作者标识；系统自身发起时为 <c>null</c>。</summary>
    public string? ActorId { get; }

    /// <summary>补充细节（通常是原始事件载荷）。</summary>
    public string? Detail { get; }

    /// <summary>记录时刻（UTC）。</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>记录一条审计。</summary>
    /// <param name="id">标识。</param>
    /// <param name="action">动作。</param>
    /// <param name="subjectType">客体类型。</param>
    /// <param name="subjectId">客体标识。</param>
    /// <param name="actorId">操作者标识。</param>
    /// <param name="detail">细节。</param>
    /// <param name="recordedAt">记录时刻。</param>
    /// <returns>成功时返回条目；必填项为空则失败。</returns>
    public static Result<AuditEntry> Record(
        AuditEntryId id,
        string? action,
        string? subjectType,
        string? subjectId,
        string? actorId,
        string? detail,
        DateTimeOffset recordedAt)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return Result.Failure<AuditEntry>(new Error("auditing.action.empty", "审计动作不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(subjectType) || string.IsNullOrWhiteSpace(subjectId))
        {
            return Result.Failure<AuditEntry>(new Error(
                "auditing.subject.empty",
                "审计客体类型与标识都不能为空。"));
        }

        return Result.Success(new AuditEntry(
            id,
            action.Trim(),
            subjectType.Trim(),
            subjectId.Trim(),
            actorId,
            detail,
            recordedAt));
    }
}
