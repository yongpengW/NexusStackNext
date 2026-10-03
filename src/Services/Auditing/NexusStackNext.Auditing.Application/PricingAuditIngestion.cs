using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Pricing.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>接纳 Pricing 明确提交的最小对象变化，不读取任何来源业务表。</summary>
/// <param name="ingestion">原子事实接纳。</param>
/// <param name="serializer">版本契约序列化。</param>
public sealed class PricingAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => PriceQuoteCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        PriceQuoteCommittedV1 message;
        try { message = serializer.Deserialize<PriceQuoteCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.ItemId == Guid.Empty
            || message.Operation is not ("created" or "inputs-changed" or "costing-applied" or "result-applied")) { return false; }
        if (message.Operation == "costing-applied" ? message.CostingItemId is null || message.CostingItemId == Guid.Empty
            : message.CostingItemId is not null) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid()
            || execution.TraceId != message.TraceId || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "pricing", "pricing.price-quote." + message.Operation,
            "price-quote", message.ItemId.ToString("D"), message.Version, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            RelatedSubject = message.CostingItemId is { } costingItem ? new AuditSubjectReference("costing", "cost-sheet", costingItem.ToString("D")) : null,
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
