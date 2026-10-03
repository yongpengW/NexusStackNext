using System.Globalization;
using System.Text.Json;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Auditing.Application;

/// <summary>接纳计划的已提交事实；调度决定只能关联 Scheduling 自己登记的决定标识。</summary>
/// <param name="ingestion">原子审计接纳。</param>
/// <param name="serializer">版本契约序列化。</param>
public sealed class SchedulingAuditIngestion(AuditIngestion ingestion, IIntegrationEventSerializer serializer) : IIntegrationEventProcessor
{
    /// <inheritdoc />
    public string EventName => PlanCommittedV1.Name;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        PlanCommittedV1 message;
        try { message = serializer.Deserialize<PlanCommittedV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.PlanId <= 0
            || message.Operation is not ("created" or "enabled" or "disabled" or "rule-changed" or "deferred" or "failure-cleared"
                or "rescheduled" or "advanced" or "triggered" or "coalesced" or "skipped")) { return false; }
        var recordsDecision = message.Operation is "triggered" or "coalesced" or "skipped";
        if (recordsDecision ? message.DecisionId is null || message.DecisionId == Guid.Empty : message.DecisionId is not null) { return false; }
        if (message.Execution is { } execution && (!execution.IsValid()
            || execution.TraceId != message.TraceId || (execution.CorrelationId is not null && execution.CorrelationId != message.CorrelationId))) { return false; }
        var fact = new AuditFact(message.EventId, EventName, "scheduling", "scheduling.plan." + message.Operation,
            "scheduled-task", message.PlanId.ToString(CultureInfo.InvariantCulture), message.Version, message.ActorId,
            message.OccurredAt, message.TraceId, message.CorrelationId)
        {
            Execution = message.Execution is { } origin
                ? new AuditExecution(origin.OperationId, origin.Source, origin.RootOperationId, origin.RootSource, origin.InitiatorId) : null,
            RelatedSubject = message.DecisionId is { } decisionId
                ? new AuditSubjectReference("scheduling", "schedule-decision", decisionId.ToString("D")) : null,
        };
        return (await ingestion.IngestAsync(fact, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }
}
