using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>只接纳 Costing 明确提交的对象变化，不接纳携带金额的业务计算事件作为审计。</summary>
/// <param name="ingestion">原子事实接纳。</param>
/// <param name="serializer">版本契约序列化。</param>
public sealed class CostingAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => CostSheetCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        CostSheetCommittedV1 message;
        try { message = serializer.Deserialize<CostSheetCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.ItemId == Guid.Empty
            || message.Operation is not ("created" or "inputs-changed" or "result-applied")) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid()
            || execution.TraceId != message.TraceId || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "costing", "costing.cost-sheet." + message.Operation,
            "cost-sheet", message.ItemId.ToString("D"), message.Version, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
