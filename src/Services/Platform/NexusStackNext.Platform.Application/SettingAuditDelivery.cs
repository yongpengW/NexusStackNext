using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Platform.Application;

/// <summary>设置审计投递的有界调查与条件重试接口。</summary>
public interface ISettingAuditDelivery : IFactDeliveryRecoveryCleanup
{
    /// <summary>按消息身份读取已提交的最小状态，不受有界列表位置限制。</summary>
    /// <param name="messageId">所属消息标识。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>可管理消息的投递状态、未找到或明确不可用。</returns>
    Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>读取所属独立恢复凭据池的已提交额度和占用，不扫描原消息正文。</summary>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>有限恢复池快照或明确不可用。</returns>
    Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default);

    /// <summary>列出指定状态中最早的消息，最多 100 条。</summary>
    /// <param name="state">Pending / Delivered / DeadLettered。</param>
    /// <param name="limit">1 到 100。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>投递状态。</returns>
    Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default);

    /// <summary>按稳定请求和双条件恢复，重放返回原裁决，不再次开放预算。</summary>
    /// <param name="request">所属消息的条件恢复请求。</param>
    /// <param name="actorId">宿主认证的操作者。</param>
    /// <param name="occurredAt">注入时钟给出的裁决时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <param name="cancellationToken">实际发布之前可取消。</param>
    /// <returns>稳定裁决或明确拒绝。</returns>
    Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default);

    /// <summary>读取保留的原恢复裁决，不改变当前投递状态。</summary>
    /// <param name="requestId">稳定请求标识。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>原凭据、未找到或明确不可用。</returns>
    Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default);
}
