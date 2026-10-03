using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingCommittedFactInterceptor(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null) : CommittedFactInterceptor<PricingDbContext>
{
    protected override IReadOnlyList<OutboxEntry> CreateFacts(PricingDbContext context)
    {
        var changes = new List<(PriceQuote Quote, string Operation)>();
        foreach (var entry in context.ChangeTracker.Entries<PriceQuote>().ToArray())
        {
            var quote = entry.Entity;
            var added = entry.State == EntityState.Added;
            if (added) { changes.Add((quote, "created")); }
            else if (entry.State != EntityState.Modified || quote.Version == entry.Property(item => item.Version).OriginalValue) { continue; }
            var costingApplied = added ? quote.CostingRevision > 0
                : quote.CostingRevision != entry.Property(item => item.CostingRevision).OriginalValue;
            if (costingApplied) { changes.Add((quote, "costing-applied")); }
            if (!added && (quote.FeeRate != entry.Property(item => item.FeeRate).OriginalValue
                || !costingApplied && quote.Cost != entry.Property(item => item.Cost).OriginalValue)) { changes.Add((quote, "inputs-changed")); }
            if (added ? quote.CalculatedRevision > 0
                : quote.CalculatedRevision != entry.Property(item => item.CalculatedRevision).OriginalValue
                    || quote.BreakEvenPrice != entry.Property(item => item.BreakEvenPrice).OriginalValue) { changes.Add((quote, "result-applied")); }
        }
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var now = clock.UtcNow;
        return changes.Select(change => OutboxEntry.From(new PriceQuoteCommittedV1
        {
            ItemId = change.Quote.Id.Value,
            Operation = change.Operation,
            Version = change.Quote.Version,
            CostingItemId = change.Operation == "costing-applied" ? change.Quote.Id.Value : null,
            ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
            OccurredAt = now,
            TraceId = trace,
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        }, serializer)).ToArray();
    }
}
