using System.Globalization;
using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>接纳 Identity 的已知最小事实，不能由消息自由指定来源或审计动作。</summary>
/// <param name="ingestion">原子审计接纳。</param>
/// <param name="serializer">契约序列化。</param>
public sealed class IdentityAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => IdentityEntityCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        IdentityEntityCommittedV1 message;
        try { message = serializer.Deserialize<IdentityEntityCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        var known = (message.SubjectType, message.Operation) is ("user", "created" or "login-failed" or "login-locked" or "role-assigned" or "role-revoked" or "login-succeeded" or "sessions-revoked"
            or "contact-changed" or "password-changed" or "enabled" or "disabled")
            or ("role", "created" or "menu-granted" or "menu-revoked" or "renamed" or "platforms-changed") or ("refresh-token", "issued" or "consumed" or "revoked")
            or ("menu-tree", "created" or "node-added" or "node-removed" or "node-moved" or "node-ancestry-changed" or "node-renamed" or "node-reordered")
            or ("api-resource", "registered");
        if (message.EventId != envelope.MessageId || !known
            || !long.TryParse(message.SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || id.ToString(CultureInfo.InvariantCulture) != message.SubjectId) { return false; }
        var relatedType = (message.SubjectType, message.Operation) switch
        {
            ("refresh-token", _) => "user",
            ("user", "role-assigned" or "role-revoked") => "role",
            ("role", "menu-granted" or "menu-revoked") => "menu",
            ("menu-tree", not "created") => "menu",
            ("api-resource", "registered") when message.RelatedSubject is not null => "menu",
            _ => null,
        };
        if (relatedType is null ? message.RelatedSubject is not null
            : message.RelatedSubject is not { Id: > 0 } || message.RelatedSubject.Type != relatedType) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid()
            || execution.TraceId != message.TraceId || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "identity", "identity." + message.SubjectType + "." + message.Operation,
            message.SubjectType, message.SubjectId, message.Version, message.ActorId, message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
            RelatedSubject = message.RelatedSubject is { } related
                ? new AuditSubjectReference("identity", related.Type, related.Id.ToString(CultureInfo.InvariantCulture)) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
