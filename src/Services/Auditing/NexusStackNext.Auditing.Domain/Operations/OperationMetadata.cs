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
    internal bool IsValid() => Safe(Action, 200) && ExecutionRole is "endpoint" or "proxy"
        && (Description is null || Safe(Description, 256)) && ValidSubject()
        && ValidSpan(SpanId) && ValidSpan(ParentSpanId)
        && (CorrelationId is null || CorrelationId.Length is > 0 and <= 64
            && CorrelationId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

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
