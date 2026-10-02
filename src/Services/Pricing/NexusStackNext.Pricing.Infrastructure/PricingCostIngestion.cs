using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingCostIngestion(PricingDbContext database) : IIntegrationEventProcessor
{
    public string EventName => CostCalculatedV1.Name;
    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        CostCalculatedV1 cost;
        try { cost = new SystemTextJsonIntegrationEventSerializer().Deserialize<CostCalculatedV1>(envelope.Payload); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (cost.EventId != envelope.MessageId || cost.ItemId == Guid.Empty || cost.CostRevision <= 0
            || !PriceQuote.IsValidInput(cost.UnitCost, 0)) { return false; }

        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        // 插入与业务更新、任务登记同事务；并发重复等待前一事务提交或回滚。
        var inbox = new EfInboxStore<PricingDbContext>(database);
        if (!await inbox.TryBeginProcessingAsync("pricing-cost", EventName, envelope.MessageId, now, cancellationToken).ConfigureAwait(false)) { return true; }
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-request/" + envelope.MessageId}, 0))", cancellationToken).ConfigureAwait(false);
        // 手工请求与上游事件不能共用一个任务标识。
        if (await database.Tasks.AnyAsync(x => x.TaskId == envelope.MessageId, cancellationToken).ConfigureAwait(false)) { return false; }
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-item/" + cost.ItemId}, 0))", cancellationToken).ConfigureAwait(false);
        var id = new PriceId(cost.ItemId);
        var quote = await database.Quotes.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (quote is null)
        {
            // 演示默认费率为零；后续费率只经 Pricing 的命令更新。
            quote = PriceQuote.Create(id, cost.UnitCost, 0m).Value;
            database.Quotes.Add(quote);
        }
        var applied = quote.ApplyCostingCost(cost.CostRevision, cost.UnitCost);
        if (applied.IsFailure) { return false; }
        if (applied.Value)
        {
            database.Tasks.Add(new RecalculationEntry
            {
                TaskId = envelope.MessageId,
                ItemId = id,
                Origin = "costing",
                Cost = quote.Cost,
                FeeRate = quote.FeeRate,
                InputRevision = quote.InputRevision,
            });
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
