using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>安全的数值策略额度，不含业务值。</summary>
/// <param name="MaxRecords">最大条数。</param>
/// <param name="MaxPayloadBytes">最大 UTF-8 总字节。</param>
/// <param name="MaxRecordPayloadBytes">最大单条 UTF-8 字节。</param>
public sealed record FactCapacityPolicyLimits(long MaxRecords, long MaxPayloadBytes, int MaxRecordPayloadBytes);

/// <summary>所属来源容量策略已经改变的不可变控制事实。</summary>
public abstract record FactCapacityPolicyChanged : IntegrationEvent
{
    /// <summary>原请求标识，不代替独立事件身份。</summary>
    public required Guid RequestId { get; init; }
    /// <summary>改变后的策略版本。</summary>
    public required long PolicyRevision { get; init; }
    /// <summary>此前额度。</summary>
    public required FactCapacityPolicyLimits Previous { get; init; }
    /// <summary>改变后的额度。</summary>
    public required FactCapacityPolicyLimits Current { get; init; }
    /// <summary>固定理由。</summary>
    public required string Reason { get; init; }
    /// <summary>受信操作者。</summary>
    public required string ActorId { get; init; }
    /// <summary>来源执行追踪。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全关联标识。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>实际执行关联。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
}
