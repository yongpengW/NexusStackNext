using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>已裁决的固定数值变化；来源适配器只把它投影为自己的 Contracts 事件。</summary>
/// <param name="RequestId">条件请求标识，不代替独立事件身份。</param>
/// <param name="PolicyRevision">新策略版本。</param>
/// <param name="Previous">旧额度。</param>
/// <param name="Current">新额度。</param>
/// <param name="Reason">固定操作理由。</param>
/// <param name="ActorId">经过来源认证与授权的操作者。</param>
/// <param name="OccurredAt">注入时钟给出的发生时间。</param>
/// <param name="TraceId">来源执行追踪。</param>
/// <param name="CorrelationId">安全关联。</param>
/// <param name="Execution">来源执行信息。</param>
public sealed record FactCapacityPolicyChange(Guid RequestId, long PolicyRevision, FactCapacityPolicyLimits Previous,
    FactCapacityPolicyLimits Current, string Reason, string ActorId, DateTimeOffset OccurredAt, string TraceId,
    string CorrelationId, ExecutionOrigin? Execution);
