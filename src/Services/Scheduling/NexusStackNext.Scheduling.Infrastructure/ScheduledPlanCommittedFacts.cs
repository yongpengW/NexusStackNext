using System.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure;

internal sealed class ScheduledPlanCommittedFacts(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null)
{
    public IReadOnlyList<OutboxEntry> Create(ScheduledTask? before, ScheduledTask after, ScheduleDecision? decision = null)
    {
        if (before is not null && before.Version == after.Version) { return []; }
        var operations = new List<string>();
        if (before is null) { operations.Add("created"); }
        else
        {
            if (before.IsEnabled != after.IsEnabled) { operations.Add(after.IsEnabled ? "enabled" : "disabled"); }
            if (before.Rule != after.Rule) { operations.Add("rule-changed"); }
            if (after.RetryAt is not null && (before.RetryAt != after.RetryAt || before.SchedulingFailureCount != after.SchedulingFailureCount)) { operations.Add("deferred"); }
            if (before.SchedulingFailureCount > 0 && after.SchedulingFailureCount == 0) { operations.Add("failure-cleared"); }
            if (decision is not null)
            {
                operations.Add(decision.Kind switch
                {
                    "Triggered" => "triggered",
                    "Coalesced" => "coalesced",
                    "Skipped" => "skipped",
                    _ => throw new InvalidOperationException("未知的调度决定不能成为提交事实。"),
                });
            }
            else if (before.TriggerSequence != after.TriggerSequence) { operations.Add("advanced"); }
            else if (before.IsEnabled == after.IsEnabled && before.Rule == after.Rule && before.NextRunAt != after.NextRunAt) { operations.Add("rescheduled"); }
        }
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var at = clock.UtcNow;
        return operations.Select(operation => OutboxEntry.From(new PlanCommittedV1
        {
            PlanId = after.Id.Value,
            Version = after.Version,
            Operation = operation,
            DecisionId = operation is "triggered" or "coalesced" or "skipped" ? decision?.DecisionId : null,
            ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
            OccurredAt = at,
            TraceId = trace,
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        }, serializer)).ToArray();
    }
}
