using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>来源日志的有界维护；不提供任意删除观察或业务事实的能力。</summary>
public interface IOperationJournalMaintenance
{
    /// <summary>读取安全交付状态；已清理的来源副本不再可查。</summary>
    /// <param name="messageId">消息身份。</param>
    /// <param name="cancellationToken">取消预算。</param>
    /// <returns>当前状态或不存在。</returns>
    Task<Result<OperationJournalDelivery>> GetDeliveryAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>按来源有界分页读取停止投递的记录。</summary>
    /// <param name="query">有界查询条件。</param>
    /// <param name="cancellationToken">取消预算。</param>
    /// <returns>安全状态页或无效参数。</returns>
    Task<Result<OperationJournalDeadLetterPage>> QueryDeadLettersAsync(OperationJournalDeadLetterQuery query, CancellationToken cancellationToken = default);

    /// <summary>仅当停止时刻与恢复版本仍匹配时重开预算；保留原消息身份与载荷。</summary>
    /// <param name="request">稳定请求身份与读取到的停止状态。</param>
    /// <param name="actor">可信维护适配器提供的执行标签。</param>
    /// <param name="cancellationToken">取消预算。</param>
    /// <returns>原子保存的恢复凭据，或过期状态/请求身份冲突。</returns>
    Task<Result<OperationJournalRecoveryReceipt>> RetryDeliveryAsync(OperationJournalRecoveryRequest request,
        OperationJournalRecoveryActor actor, CancellationToken cancellationToken = default);

    /// <summary>读取恢复凭据，不依赖目标消息是否已被清理。</summary>
    /// <param name="requestId">恢复请求身份。</param>
    /// <param name="cancellationToken">查询预算。</param>
    /// <returns>不可变恢复凭据或不存在。</returns>
    Task<Result<OperationJournalRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>按配置保留期与时钟清理一批已交付记录，并在同次提交释放容量。</summary>
    /// <param name="cancellationToken">维护操作自身的取消预算。</param>
    /// <returns>本次清理提交的记录数；待投递、死信和保留期内的记录不会被删除。</returns>
    Task<int> CleanupDeliveredAsync(CancellationToken cancellationToken = default);

    /// <summary>按凭据保存时确定的到期时间清理一批记录，释放恢复额度；不修改消息交付状态。</summary>
    /// <param name="cancellationToken">维护操作的取消预算。</param>
    /// <returns>本次成功清理的凭据数。</returns>
    Task<int> CleanupRecoveryRecordsAsync(CancellationToken cancellationToken = default);
}
