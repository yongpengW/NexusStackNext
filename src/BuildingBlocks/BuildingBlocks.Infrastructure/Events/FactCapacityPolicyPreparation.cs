using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

internal static class FactCapacityPolicyPreparation
{
    internal static bool IsValid(FactCapacityPolicyRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution)
        => request.IsValid && !string.IsNullOrWhiteSpace(actorId) && actorId.Length <= 200 && !actorId.Any(char.IsControl)
            && (execution is null || execution.IsValid()) && occurredAt <= DateTimeOffset.MaxValue.AddDays(-7);

    internal static PreparedChange Prepare(FactCapacityPolicyRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, FactCapacityPolicyLimits previous, long currentRevision,
        FactCapacityPolicySource source, IIntegrationEventSerializer serializer)
    {
        var next = request.Limits;
        var changed = previous != next;
        var revision = changed ? checked(currentRevision + 1) : currentRevision;
        OutboxEntry? fact = null;
        if (changed)
        {
            var trace = execution?.TraceId ?? Guid.NewGuid().ToString("N");
            fact = OutboxEntry.From(source.CreateFact(new(request.RequestId, revision, previous, next,
                request.Reason, actorId, occurredAt, trace, execution?.CorrelationId ?? trace, execution)), serializer);
        }
        var receipt = new FactCapacityPolicyReceipt(request.RequestId, revision, changed, previous, next,
            fact?.Id, occurredAt, occurredAt.AddDays(7));
        var json = JsonSerializer.Serialize(new { request, actorId, receipt });
        var size = checked(Encoding.UTF8.GetByteCount(json) + (fact is null ? 0 : Encoding.UTF8.GetByteCount(fact.Payload)));
        return new(receipt, fact, json, size);
    }

    internal sealed record PreparedChange(FactCapacityPolicyReceipt Receipt, OutboxEntry? Fact, string Json, int PayloadBytes);
}
