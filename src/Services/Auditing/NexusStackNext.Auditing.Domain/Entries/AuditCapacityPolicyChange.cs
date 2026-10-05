namespace NexusStackNext.Auditing.Domain.Entries;

/// <summary>容量治理中的三个明确数值，不包含任意业务字段。</summary>
/// <param name="MaxRecords">最大保留条数。</param>
/// <param name="MaxPayloadBytes">最大总载荷字节。</param>
/// <param name="MaxRecordPayloadBytes">最大单条载荷字节。</param>
public sealed record AuditCapacityPolicyLimits(long MaxRecords, long MaxPayloadBytes, int MaxRecordPayloadBytes);

/// <summary>来源已经提交的容量变化，保留请求身份、策略版本与前后数值。</summary>
/// <param name="RequestId">来源裁决的请求身份，不代替消息身份。</param>
/// <param name="PolicyRevision">改变后的策略版本。</param>
/// <param name="Reason">固定治理理由。</param>
public sealed record AuditCapacityPolicyChange(Guid RequestId, long PolicyRevision, string Reason)
{
    /// <summary>改变前的三个额度。</summary>
    public required AuditCapacityPolicyLimits Previous { get; init; }
    /// <summary>改变后的三个额度。</summary>
    public required AuditCapacityPolicyLimits Current { get; init; }
}
