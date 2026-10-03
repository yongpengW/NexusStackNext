using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>由可信维护适配器提供的本机执行标签，不是业务身份认证证明。</summary>
/// <param name="Account">操作系统账户标签。</param>
/// <param name="Machine">执行机器标签。</param>
public sealed record OperationJournalRecoveryActor(string Account, string Machine);

/// <summary>一次可重放的条件恢复请求；请求身份由调用方稳定提供。</summary>
/// <param name="RequestId">恢复请求身份。</param>
/// <param name="MessageId">目标消息身份。</param>
/// <param name="ExpectedDeadLetteredAt">读到的停止时间。</param>
/// <param name="ExpectedRetryRevision">读到的恢复版本。</param>
/// <param name="Reason">固定原因：dependency-restored 或 manual-retry。</param>
public sealed record OperationJournalRecoveryRequest(Guid RequestId, Guid MessageId, DateTimeOffset ExpectedDeadLetteredAt,
    long ExpectedRetryRevision, string Reason)
{
    /// <summary>验证请求与适配器提供的有界执行标签。</summary>
    /// <param name="actor">可信适配器提供的标签。</param>
    /// <returns>有效或稳定错误。</returns>
    public Result Validate(OperationJournalRecoveryActor actor) => RequestId != Guid.Empty && MessageId != Guid.Empty
        && ExpectedRetryRevision >= 0 && Reason is "dependency-restored" or "manual-retry"
        && actor is not null && ValidLabel(actor.Account) && ValidLabel(actor.Machine)
        ? Result.Success() : Result.Failure(OperationJournalRecoveryErrors.Invalid);

    private static bool ValidLabel(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
}

/// <summary>成功恢复的不可变凭据；当前投递状态须另行查询。</summary>
/// <param name="Request">原恢复请求。</param>
/// <param name="Actor">实际执行适配器提供的身份标签。</param>
/// <param name="Source">目标消息的真实来源。</param>
/// <param name="RecoveredAt">来源服务记录的恢复时刻。</param>
/// <param name="RetryRevision">恢复提交后的版本。</param>
/// <param name="RetainUntil">凭据最早可清理的时间；此前保证请求幂等重放。</param>
public sealed record OperationJournalRecoveryReceipt(OperationJournalRecoveryRequest Request, OperationJournalRecoveryActor Actor,
    string Source, DateTimeOffset RecoveredAt, long RetryRevision, DateTimeOffset RetainUntil);

/// <summary>恢复控制操作的固定错误。</summary>
public static class OperationJournalRecoveryErrors
{
    /// <summary>请求或执行标签无效。</summary>
    public static readonly Error Invalid = new("operation_journal.recovery_invalid", "恢复请求无效。");
    /// <summary>同一请求身份已经用于不同内容。</summary>
    public static readonly Error RequestConflict = new("operation_journal.recovery_request_conflict", "恢复请求身份已用于不同内容。");
    /// <summary>恢复记录不存在或已经超过保留期并被清理。</summary>
    public static readonly Error NotFound = new("operation_journal.recovery_not_found", "恢复记录不存在或已清理。");
    /// <summary>独立恢复记录额度已满，没有执行恢复。</summary>
    public static readonly Error CapacityExceeded = new("operation_journal.recovery_capacity_exceeded", "恢复记录额度已满，未执行恢复。");
}
