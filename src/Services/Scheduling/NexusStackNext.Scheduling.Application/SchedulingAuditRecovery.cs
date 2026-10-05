using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Scheduling.Application;

/// <summary>计划事实恢复端口，实现在所属存储内原子发布。</summary>
public interface ISchedulingAuditDelivery : IFactDeliveryRecoveryCleanup
{
    /// <summary>按消息身份读取已提交的最小状态，不受列表位置限制。</summary>
    /// <param name="messageId">所属消息标识。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>可管理的状态、未找到或明确不可用。</returns>
    Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>按发生时刻和消息身份列出指定状态，最多一百条。</summary>
    /// <param name="state">Pending / Delivered / DeadLettered。</param>
    /// <param name="limit">一至一百。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>不含消息正文及异常的最小投递状态。</returns>
    Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default);

    /// <summary>读取独立恢复凭据池的额度与已提交占用。</summary>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>安全容量快照或明确不可用。</returns>
    Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default);

    /// <summary>双条件恢复或重放原裁决，不修改计划、调度决定或业务触发消息。</summary>
    /// <param name="request">稳定条件请求。</param>
    /// <param name="actorId">宿主认证的操作者。</param>
    /// <param name="occurredAt">可信裁决时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <param name="cancellationToken">实际发布之前可取消。</param>
    /// <returns>原恢复凭据或明确拒绝。</returns>
    Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default);

    /// <summary>读取保留的原恢复裁决，供响应丢失后的核对。</summary>
    /// <param name="requestId">稳定请求。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>原凭据、未找到或明确不可用。</returns>
    Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default);
}

/// <summary>Scheduling 自己的恢复拒绝语义。</summary>
public static class SchedulingAuditRecoveryErrors
{
    /// <summary>所属可管理消息不存在。</summary>
    public static readonly Error DeliveryNotFound = new("scheduling.delivery_not_found", "未找到可管理的事实投递。");
    /// <summary>消息存在，但不属于模块声明的可管理事实。</summary>
    public static readonly Error Unmanaged = new("scheduling.delivery_recovery.unmanaged", "该消息不属于可管理的事实投递。");
    /// <summary>没有保留该请求的原恢复凭据。</summary>
    public static readonly Error NotFound = new("scheduling.delivery_recovery.not_found", "恢复凭据不存在。");
    /// <summary>固定条件或可信执行信息无效。</summary>
    public static readonly Error Invalid = new("scheduling.delivery_recovery.invalid", "恢复请求无效。");
    /// <summary>原消息的停止证据或恢复代次已改变。</summary>
    public static readonly Error Conflict = new("scheduling.delivery_conflict", "投递状态已经改变，请重新读取。");
    /// <summary>同一请求身份不能替换操作者或内容。</summary>
    public static readonly Error RequestConflict = new("scheduling.delivery_recovery.request_conflict", "恢复请求身份已用于不同裁决。");
    /// <summary>独立有限恢复池已满，整个恢复拒绝。</summary>
    public static readonly Error Exhausted = new("scheduling.delivery_recovery.exhausted", "恢复凭据容量已满。");
}
