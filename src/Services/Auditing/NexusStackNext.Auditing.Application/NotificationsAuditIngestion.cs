using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Notifications.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>只接纳 Notifications 明确声明的邮件生命周期提交事实。</summary>
public sealed class NotificationsAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => EmailDeliveryCommittedV1.Name;
    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        EmailDeliveryCommittedV1 message;
        try { message = serializer.Deserialize<EmailDeliveryCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.EmailId == Guid.Empty || message.Attempt < 0
            || message.Operation is not ("submitted" or "attempt-started" or "accepted" or "failed" or "retry" or "resend" or "confirm")) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid() || execution.TraceId != message.TraceId
            || execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId)) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "notifications", "notifications.email." + message.Operation,
            "email-delivery", message.EmailId.ToString("D"), message.Version, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
