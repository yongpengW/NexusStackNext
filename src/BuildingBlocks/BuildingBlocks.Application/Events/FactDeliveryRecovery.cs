using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属事实的稳定条件恢复输入，来源与操作者由宿主决定。</summary>
/// <param name="RequestId">稳定请求身份。</param>
/// <param name="MessageId">所属原消息。</param>
/// <param name="ExpectedDeadLetteredAt">观察到的停止时刻。</param>
/// <param name="ExpectedRetryRevision">观察到的恢复代次。</param>
/// <param name="Reason">manual-retry 或 dependency-restored。</param>
public sealed record FactDeliveryRecoveryRequest(Guid RequestId, Guid MessageId, DateTimeOffset ExpectedDeadLetteredAt,
    long ExpectedRetryRevision, string Reason)
{
    /// <summary>固定输入约束；派生校验不进入 HTTP 契约。</summary>
    [JsonIgnore]
    public bool IsValid => RequestId != Guid.Empty && MessageId != Guid.Empty && ExpectedDeadLetteredAt != default
        && ExpectedRetryRevision >= 0 && Reason is "manual-retry" or "dependency-restored";

    /// <summary>同时校验可信执行信息及固定七天期限的可表示性。</summary>
    /// <param name="actorId">宿主认证的操作者。</param>
    /// <param name="occurredAt">注入时钟给出的裁决时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <returns>是否可以准备恢复裁决。</returns>
    public bool IsValidFor(string actorId, DateTimeOffset occurredAt, ExecutionOrigin? execution)
        => IsValid && !string.IsNullOrWhiteSpace(actorId) && actorId.Length <= 200 && !actorId.Any(char.IsControl)
            && (execution is null || execution.IsValid()) && occurredAt <= DateTimeOffset.MaxValue.AddDays(-7)
            && occurredAt.DateTime <= DateTime.MaxValue.AddDays(-7);
}

/// <summary>来源对一次恢复的原裁决，不表示消息当前状态或中央保存完成。</summary>
/// <param name="RequestId">稳定请求。</param>
/// <param name="MessageId">原消息身份。</param>
/// <param name="Source">代码声明的来源。</param>
/// <param name="ActorId">宿主认证的操作者。</param>
/// <param name="Reason">固定恢复原因。</param>
/// <param name="ExpectedDeadLetteredAt">原停止证据。</param>
/// <param name="ExpectedRetryRevision">原恢复代次。</param>
/// <param name="RetryRevision">已接受的新代次。</param>
/// <param name="RecoveredAt">可信接受时刻。</param>
/// <param name="RetainUntil">固定最早保留期限。</param>
/// <param name="Execution">原可信执行关联。</param>
public sealed record FactDeliveryRecoveryReceipt(Guid RequestId, Guid MessageId, string Source, string ActorId,
    string Reason, DateTimeOffset ExpectedDeadLetteredAt, long ExpectedRetryRevision, long RetryRevision,
    DateTimeOffset RecoveredAt, DateTimeOffset RetainUntil, ExecutionOrigin? Execution)
{
    /// <summary>重放只比较原请求身份、条件、理由和操作者，不读取当前投递状态。</summary>
    /// <param name="request">重放请求。</param>
    /// <param name="actorId">宿主认证的操作者。</param>
    /// <returns>是否重放同一原裁决。</returns>
    public bool Matches(FactDeliveryRecoveryRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestId == request.RequestId && MessageId == request.MessageId && ActorId == actorId
            && ExpectedDeadLetteredAt == request.ExpectedDeadLetteredAt
            && ExpectedRetryRevision == request.ExpectedRetryRevision && Reason == request.Reason;
    }
}

/// <summary>只包含所属恢复凭据池的计量，不包含原消息或操作者。</summary>
/// <param name="Source">代码声明的所属来源。</param>
/// <param name="IsPersistent">是否跨进程持久保存。</param>
/// <param name="Capacity">与业务事实及策略控制池分开的上限和占用。</param>
public sealed record FactDeliveryRecoveryCapacity(string Source, bool IsPersistent, FactCapacityControlSnapshot Capacity);
