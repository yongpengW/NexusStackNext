using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Auditing.Application;

/// <summary>按模块声明的具体 Contracts 接纳容量变化，来源与动作不来自载荷。</summary>
/// <typeparam name="TEvent">来源固定版本的容量契约。</typeparam>
/// <param name="ingestion">原子审计接纳。</param>
/// <param name="serializer">固定来源契约序列化。</param>
/// <param name="source">模块代码声明的受信来源。</param>
/// <param name="eventName">模块代码声明的事件名。</param>
public sealed class FactCapacityPolicyAuditIngestion<TEvent>(AuditIngestion ingestion, IIntegrationEventSerializer serializer,
    string source, string eventName) : IIntegrationEventProcessor where TEvent : FactCapacityPolicyChanged
{
    /// <inheritdoc />
    public string EventName => eventName;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        TEvent message;
        try { message = serializer.Deserialize<TEvent>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventName != EventName || message.EventId != envelope.MessageId || message.Previous is null || message.Current is null) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid() || execution.TraceId != message.TraceId
            || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, source, source + ".fact-capacity-policy.changed",
            "fact-capacity-policy", source, message.PolicyRevision, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            CapacityPolicyChange = new(message.RequestId, message.PolicyRevision, message.Reason)
            {
                Previous = new(message.Previous.MaxRecords, message.Previous.MaxPayloadBytes, message.Previous.MaxRecordPayloadBytes),
                Current = new(message.Current.MaxRecords, message.Current.MaxPayloadBytes, message.Current.MaxRecordPayloadBytes)
            },
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
