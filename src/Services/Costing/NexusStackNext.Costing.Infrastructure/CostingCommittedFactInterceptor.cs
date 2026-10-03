using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostingCommittedFactInterceptor(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null) : CommittedFactInterceptor<CostingDbContext>
{
    protected override IReadOnlyList<OutboxEntry> CreateFacts(CostingDbContext context)
    {
        var changes = new List<(CostSheet Sheet, string Operation)>();
        foreach (var entry in context.ChangeTracker.Entries<CostSheet>().ToArray())
        {
            var sheet = entry.Entity;
            if (entry.State == EntityState.Added) { changes.Add((sheet, "created")); }
            else
            {
                if (entry.State != EntityState.Modified || sheet.Version == entry.Property(item => item.Version).OriginalValue) { continue; }
                if (sheet.PurchaseCost != entry.Property(item => item.PurchaseCost).OriginalValue
                    || sheet.FreightCost != entry.Property(item => item.FreightCost).OriginalValue) { changes.Add((sheet, "inputs-changed")); }
            }
            if (entry.State == EntityState.Added ? sheet.CalculatedRevision > 0
                : sheet.CalculatedRevision != entry.Property(item => item.CalculatedRevision).OriginalValue
                    || sheet.UnitCost != entry.Property(item => item.UnitCost).OriginalValue) { changes.Add((sheet, "result-applied")); }
        }
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var now = clock.UtcNow;
        return changes.Select(change => OutboxEntry.From(new CostSheetCommittedV1
        {
            ItemId = change.Sheet.Id.Value,
            Operation = change.Operation,
            Version = change.Sheet.Version,
            ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
            OccurredAt = now,
            TraceId = trace,
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        }, serializer)).ToArray();
    }
}
