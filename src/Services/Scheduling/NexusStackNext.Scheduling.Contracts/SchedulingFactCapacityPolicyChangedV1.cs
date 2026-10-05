using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Scheduling.Contracts;

/// <summary>所属 Scheduling 容量策略已经改变的不可变控制事实。</summary>
public sealed record SchedulingFactCapacityPolicyChangedV1 : FactCapacityPolicyChanged
{
    /// <summary>固定来源事件名，不与计划业务事实额度混计。</summary>
    public const string Name = "scheduling.fact-capacity-policy-changed.v1";
    /// <inheritdoc />
    public override string EventName => Name;

    /// <summary>把已裁决的安全数值投影为本上下文固定契约。</summary>
    /// <param name="change">所属存储已经准备的变化。</param>
    /// <returns>独立事件身份的固定控制事实。</returns>
    public static SchedulingFactCapacityPolicyChangedV1 From(FactCapacityPolicyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new()
        {
            RequestId = change.RequestId,
            PolicyRevision = change.PolicyRevision,
            Previous = change.Previous,
            Current = change.Current,
            Reason = change.Reason,
            ActorId = change.ActorId,
            OccurredAt = change.OccurredAt,
            TraceId = change.TraceId,
            CorrelationId = change.CorrelationId,
            Execution = change.Execution,
        };
    }
}
