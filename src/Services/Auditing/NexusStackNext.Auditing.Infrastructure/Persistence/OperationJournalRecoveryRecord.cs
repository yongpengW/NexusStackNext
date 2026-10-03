using NexusStackNext.Auditing.Application;

namespace NexusStackNext.Auditing.Infrastructure.Persistence;

internal sealed class OperationJournalRecoveryRecord
{
    public Guid RequestId { get; set; }
    public Guid MessageId { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset StoppedAt { get; set; }
    public long PreviousRetryRevision { get; set; }
    public long RetryRevision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public DateTimeOffset RecoveredAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }

    public OperationJournalRecoveryReceipt Receipt() => new(
        new(RequestId, MessageId, StoppedAt, PreviousRetryRevision, Reason), new(Account, Machine), Source, RecoveredAt, RetryRevision, RetainUntil);

    public static OperationJournalRecoveryRecord From(OperationJournalRecoveryReceipt receipt) => new()
    {
        RequestId = receipt.Request.RequestId,
        MessageId = receipt.Request.MessageId,
        Source = receipt.Source,
        StoppedAt = receipt.Request.ExpectedDeadLetteredAt.ToUniversalTime(),
        PreviousRetryRevision = receipt.Request.ExpectedRetryRevision,
        RetryRevision = receipt.RetryRevision,
        Reason = receipt.Request.Reason,
        Account = receipt.Actor.Account,
        Machine = receipt.Actor.Machine,
        RecoveredAt = receipt.RecoveredAt.ToUniversalTime(),
        RetainUntil = receipt.RetainUntil.ToUniversalTime(),
    };
}
