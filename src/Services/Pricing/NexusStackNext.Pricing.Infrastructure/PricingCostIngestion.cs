using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Pricing.Domain;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingCostIngestion(PricingDbContext database, IBackgroundExecutionObservation observations, IExecutionContext execution) : IIntegrationEventProcessor
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
            || !PriceQuote.IsValidInput(cost.UnitCost, 0) || cost.ExecutionOrigin is { } origin && !origin.IsValid()) { return false; }

        try
        {
            var result = await observations.ObserveAsync(new MessageExecutionDescriptor("pricing.cost.accept", EventName, envelope.MessageId),
                () => Task.FromResult(new BackgroundExecutionInput<CostCalculatedV1>(cost, cost.ExecutionOrigin)),
                input => ReceiveAsync(envelope, input, cancellationToken), static received => received.Outcome, cancellationToken).ConfigureAwait(false);
            return result.Acknowledged;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "P0001", ConstraintName: "pricing_fact_capacity_exhausted" })
        {
            // 失败已由观察适配器登记，本地事务已回滚；不得确认消息或保留持久去重结果。
            database.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<(bool Acknowledged, BackgroundExecutionOutcome Outcome)> ReceiveAsync(EventEnvelope envelope, CostCalculatedV1 cost, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        // 插入与业务更新、任务登记同事务；并发重复等待前一事务提交或回滚。
        var inbox = new EfInboxStore<PricingDbContext>(database);
        var first = await inbox.TryBeginProcessingAsync("pricing-cost", EventName, envelope.MessageId, now, cancellationToken).ConfigureAwait(false);
        var receipt = await database.Inbox.SingleAsync(x => x.ConsumerName == "pricing-cost" && x.EventName == EventName
            && x.MessageId == envelope.MessageId, cancellationToken).ConfigureAwait(false);
        var fingerprint = database.Entry(receipt).Property<string?>(PricingDbContext.CostPayloadHashProperty);
        var canonical = FormattableString.Invariant($"{cost.ItemId:D}|{cost.CostRevision}|{cost.UnitCost:G29}|{cost.OccurredAt.UtcTicks}");
        if (cost.ExecutionOrigin is not null) { canonical += "|origin:" + System.Text.Json.JsonSerializer.Serialize(cost.ExecutionOrigin); }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        if (!first)
        {
            return fingerprint.CurrentValue == hash ? (true, BackgroundExecutionOutcome.Duplicate) : (false, BackgroundExecutionOutcome.Rejected);
        }
        // 包括被忽略的旧版本：同一身份以后也不能换成另一项工作。
        fingerprint.CurrentValue = hash;
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-request/" + envelope.MessageId}, 0))", cancellationToken).ConfigureAwait(false);
        // 手工请求与上游事件不能共用一个任务标识。
        if (await database.Tasks.AnyAsync(x => x.TaskId == envelope.MessageId, cancellationToken).ConfigureAwait(false)) { return (false, BackgroundExecutionOutcome.Rejected); }
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
        if (applied.IsFailure) { return (false, BackgroundExecutionOutcome.Rejected); }
        if (applied.Value)
        {
            database.Tasks.Add(new RecalculationEntry
            {
                TaskId = envelope.MessageId,
                ItemId = id,
                Origin = "costing",
                ExecutionOrigin = execution.Capture() ?? cost.ExecutionOrigin,
                Cost = quote.Cost,
                FeeRate = quote.FeeRate,
                InputRevision = quote.InputRevision,
            });
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (true, applied.Value ? BackgroundExecutionOutcome.Accepted : BackgroundExecutionOutcome.Skipped);
    }
}
