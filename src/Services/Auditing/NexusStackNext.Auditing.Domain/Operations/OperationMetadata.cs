using System.Globalization;

namespace NexusStackNext.Auditing.Domain.Operations;

/// <summary>明确提供的执行描述及关联证据；没有它时不能推测客体。</summary>
/// <param name="Action">稳定动作。</param>
/// <param name="ExecutionRole">本地处理或代理转发。</param>
/// <param name="Description">静态描述。</param>
/// <param name="SubjectType">客体类型。</param>
/// <param name="SubjectIdKind">guid 或 int64。</param>
/// <param name="SubjectId">规范化的客体标识字符串。</param>
/// <param name="SpanId">当前执行 span。</param>
/// <param name="ParentSpanId">已观察到的父 span。</param>
/// <param name="CorrelationId">有界关联值。</param>
public sealed record OperationMetadata(string Action, string ExecutionRole, string? Description = null,
    string? SubjectType = null, string? SubjectIdKind = null, string? SubjectId = null,
    string? SpanId = null, string? ParentSpanId = null, string? CorrelationId = null)
{
    /// <summary>原始操作标识。</summary>
    public Guid? RootOperationId { get; init; }
    /// <summary>原始操作来源。</summary>
    public string? RootSource { get; init; }
    /// <summary>直接触发操作标识。</summary>
    public Guid? ParentOperationId { get; init; }
    /// <summary>直接触发操作来源。</summary>
    public string? ParentSource { get; init; }
    /// <summary>原发起人，与当前 Actor 分开。</summary>
    public string? InitiatorId { get; init; }
    /// <summary>所属来源的业务任务。</summary>
    public Guid? TaskId { get; init; }
    /// <summary>本次执行权的代次。</summary>
    public long? TaskEpoch { get; init; }
    /// <summary>本次裁决所属计划。</summary>
    public long? SchedulePlanId { get; init; }
    /// <summary>裁决前读取的计划版本。</summary>
    public long? ScheduleExpectedVersion { get; init; }
    /// <summary>本次拟登记的决定标识。</summary>
    public Guid? ScheduleDecisionId { get; init; }

    internal bool IsValid() => Safe(Action, 200) && ExecutionRole is "endpoint" or "proxy" or "command" or "task" or "schedule" or "recovery" or "message"
        && (Description is null || Safe(Description, 256)) && ValidSubject() && ValidExecution()
        && ValidSpan(SpanId) && ValidSpan(ParentSpanId)
        && (CorrelationId is null || CorrelationId.Length is > 0 and <= 64
            && CorrelationId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

    private bool ValidExecution()
    {
        if (RootOperationId is null && RootSource is null && ParentOperationId is null && ParentSource is null
            && InitiatorId is null && TaskId is null && TaskEpoch is null
            && SchedulePlanId is null && ScheduleExpectedVersion is null && ScheduleDecisionId is null) { return true; }
        return RootOperationId is { } root && root != Guid.Empty && Safe(RootSource, 64)
            && (ParentOperationId is null && ParentSource is null
                || ParentOperationId is { } parent && parent != Guid.Empty && Safe(ParentSource, 64))
            && (InitiatorId is null || Safe(InitiatorId, 200))
            && (TaskId is null && TaskEpoch is null || TaskId is { } task && task != Guid.Empty
                && (TaskEpoch > 0 || TaskEpoch is null && ExecutionRole == "command"))
            && (SchedulePlanId is null && ScheduleExpectedVersion is null && ScheduleDecisionId is null
                || SchedulePlanId > 0 && ScheduleExpectedVersion > 0 && ScheduleDecisionId is { } decision && decision != Guid.Empty
                && TaskId is null && TaskEpoch is null);
    }

    private bool ValidSubject()
    {
        if (SubjectType is null && SubjectIdKind is null && SubjectId is null) { return true; }
        if (!Safe(SubjectType, 100)) { return false; }
        return SubjectIdKind switch
        {
            "guid" => Guid.TryParseExact(SubjectId, "D", out var id) && id != Guid.Empty && SubjectId == id.ToString("D"),
            "int64" => long.TryParse(SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0 && SubjectId == id.ToString(CultureInfo.InvariantCulture),
            _ => false,
        };
    }

    private static bool ValidSpan(string? value) => value is null || value.Length == 16
        && value != "0000000000000000" && value.All(static character => char.IsAsciiHexDigitLower(character));

    private static bool Safe(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
}
