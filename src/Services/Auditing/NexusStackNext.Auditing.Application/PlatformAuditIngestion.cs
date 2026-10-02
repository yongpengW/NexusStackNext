using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Platform.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>仅接纳已知的设置提交契约，来源与动作由消费适配器确定。</summary>
/// <param name="ingestion">原子审计接纳。</param>
/// <param name="serializer">版本契约反序列化。</param>
public sealed class PlatformAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => SettingCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        SettingCommittedV1 message;
        try { message = serializer.Deserialize<SettingCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.Operation is not ("created" or "changed" or "cleared")) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "platform", "platform.setting." + message.Operation,
            "global-setting", message.Key, message.Version, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId);
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
