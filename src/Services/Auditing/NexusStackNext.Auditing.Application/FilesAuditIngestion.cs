using System.Globalization;
using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>接纳 Files 的明确生命周期事实，来源与动作不能由自由输入指定。</summary>
/// <param name="ingestion">原子审计接纳。</param>
/// <param name="serializer">版本契约序列化。</param>
public sealed class FilesAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => StoredFileCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        StoredFileCommittedV1 message;
        try { message = serializer.Deserialize<StoredFileCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.FileId <= 0
            || message.Operation is not ("registered" or "stored" or "published" or "expired" or "deletion-requested" or "cleanup-deferred" or "bytes-removed")) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid()
            || execution.TraceId != message.TraceId || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "files", "files.stored-file." + message.Operation,
            "stored-file", message.FileId.ToString(CultureInfo.InvariantCulture), message.Version, message.ActorId,
            message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
