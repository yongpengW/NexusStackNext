using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Domain;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class ScheduledCostIngestion(CostingDbContext database, IBackgroundExecutionObservation observations, IExecutionContext execution) : IIntegrationEventProcessor,
    IQueryHandler<GetScheduledCostReceipt, ScheduledCostReceipt>
{
    private const string ConsumerName = "costing-schedules";
    public string EventName => ScheduleTriggeredV1.Name;

    public async Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventName != EventName || envelope.MessageId == Guid.Empty) { return false; }
        ScheduleTriggeredV1 message;
        try { message = new SystemTextJsonIntegrationEventSerializer().Deserialize<ScheduleTriggeredV1>(envelope.Payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
        if (message.EventId != envelope.MessageId || message.PlanId <= 0 || message.TriggerSequence <= 0 || message.TargetId == Guid.Empty
            || message.TargetKind != CostingScheduleTarget.Recalculate || message.ScheduledAt == default || message.OccurredAt < message.ScheduledAt
            || string.IsNullOrWhiteSpace(message.CreatedBy) || message.CreatedBy.Length > 128 || message.CreatedBy.Any(char.IsControl)
            || message.ExecutionOrigin is { } origin && !origin.IsValid()) { return false; }

        var result = await observations.ObserveAsync(new MessageExecutionDescriptor("costing.schedule.accept", EventName, envelope.MessageId),
            () => Task.FromResult(new BackgroundExecutionInput<ScheduleTriggeredV1>(message, message.ExecutionOrigin)),
            input => ReceiveAsync(input, cancellationToken), static received => received.Outcome, cancellationToken).ConfigureAwait(false);
        return result.Acknowledged;
    }

    private async Task<(bool Acknowledged, BackgroundExecutionOutcome Outcome)> ReceiveAsync(ScheduleTriggeredV1 message, CancellationToken cancellationToken)
    {
        var canonical = FormattableString.Invariant($"{message.PlanId}|{message.TriggerSequence}|{message.ScheduledAt.UtcTicks}|{message.OccurredAt.UtcTicks}|{message.TargetKind}|{message.TargetId:D}|{message.CreatedBy}");
        if (message.ExecutionOrigin is not null) { canonical += "|origin:" + JsonSerializer.Serialize(message.ExecutionOrigin); }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var first = await new EfInboxStore<CostingDbContext>(database)
            .TryBeginProcessingAsync(ConsumerName, EventName, message.EventId, now, cancellationToken).ConfigureAwait(false);
        if (!first)
        {
            var existing = await database.ScheduleReceipts.AsNoTracking().SingleAsync(x => x.OccurrenceId == message.EventId, cancellationToken).ConfigureAwait(false);
            return existing.PayloadHash == hash ? (true, BackgroundExecutionOutcome.Duplicate) : (false, BackgroundExecutionOutcome.Rejected);
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-request/" + message.EventId}, 0))", cancellationToken).ConfigureAwait(false);
        string? rejection = null;
        if (await database.Tasks.AnyAsync(x => x.TaskId == message.EventId, cancellationToken).ConfigureAwait(false)
            || await database.BatchRows.AnyAsync(x => x.TaskId == message.EventId, cancellationToken).ConfigureAwait(false))
        {
            rejection = "costing.schedule_task_conflict";
        }
        else
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-item/" + message.TargetId}, 0))", cancellationToken).ConfigureAwait(false);
            var id = new CostId(message.TargetId);
            var sheet = await database.Sheets.FindAsync([id], cancellationToken).ConfigureAwait(false);
            if (sheet is null) { rejection = "costing.schedule_target_not_found"; }
            else
            {
                database.Tasks.Add(new CostCalculationEntry
                {
                    TaskId = message.EventId,
                    ItemId = id,
                    Origin = "scheduling",
                    ExecutionOrigin = execution.Capture() ?? message.ExecutionOrigin,
                    ExpectedVersion = sheet.Version,
                    PurchaseCost = sheet.PurchaseCost,
                    FreightCost = sheet.FreightCost,
                    InputRevision = sheet.InputRevision,
                });
            }
        }
        database.ScheduleReceipts.Add(new ScheduledCostReceiptEntry
        {
            OccurrenceId = message.EventId,
            PlanId = message.PlanId,
            TriggerSequence = message.TriggerSequence,
            ItemId = message.TargetId,
            CreatedBy = message.CreatedBy,
            Decision = rejection is null ? "Accepted" : "Rejected",
            TaskId = rejection is null ? message.EventId : null,
            ErrorCode = rejection,
            ReceivedAt = now,
            PayloadHash = hash,
        });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (true, rejection is null ? BackgroundExecutionOutcome.Accepted : BackgroundExecutionOutcome.Rejected);
    }

    public async Task<Result<ScheduledCostReceipt>> HandleAsync(GetScheduledCostReceipt query, CancellationToken cancellationToken = default)
    {
        var receipt = await database.ScheduleReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OccurrenceId == query.OccurrenceId, cancellationToken).ConfigureAwait(false);
        return receipt is null ? Result.Failure<ScheduledCostReceipt>(new Error("costing.not_found", "该触发尚无 Costing 接受结论。"))
            : Result.Success(receipt.ToView());
    }
}
