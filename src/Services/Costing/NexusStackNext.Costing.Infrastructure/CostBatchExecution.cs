using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed partial class CostBatchExecution(CostingDbContext database, CostingTaskOptions options,
    CostingBatchOptions batches, IBackgroundExecutionObservation observations, IExecutionContext execution)
    : ICommandHandler<ClaimCostBatch, CostBatchLease?>, ICommandHandler<ExecuteCostBatchSegment, bool>, ICommandHandler<FailCostBatch, bool>
{
    public async Task<Result<CostBatchLease?>> HandleAsync(ClaimCostBatch command, CancellationToken cancellationToken = default)
    {
        using var budget = CreateBudget(cancellationToken);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = (await database.Batches.FromSqlRaw("""
            SELECT * FROM costing.batches
            WHERE ("State" IN ('Pending','Retry') AND "AvailableAt" <= clock_timestamp())
               OR ("State" = 'Running' AND "LeaseUntil" <= clock_timestamp())
            ORDER BY "AvailableAt", "BatchId" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (entry is null) { return Result.Success<CostBatchLease?>(null); }
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (entry.State == "Running") { await FinishAttemptAsync(entry, "Expired", now, "costing.batch.lease_expired", cancellationToken).ConfigureAwait(false); }
        if (entry.Attempts >= options.MaxAttempts || entry.Epoch == long.MaxValue)
        {
            entry.State = "Failed";
            entry.ErrorCode = "costing.batch.attempts_exhausted";
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success<CostBatchLease?>(null);
        }
        entry.State = "Running";
        entry.Epoch++;
        entry.Attempts++;
        entry.ErrorCode = null;
        entry.LeaseUntil = now + options.LeaseDuration;
        entry.MaxLeaseUntil = now + options.MaxLeaseDuration;
        database.BatchAttempts.Add(new CostBatchAttemptEntry { BatchId = entry.BatchId, Epoch = entry.Epoch, StartedAt = now });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<CostBatchLease?>(new(entry.BatchId, entry.Epoch, entry.LeaseUntil.Value));
    }

    public async Task<Result<bool>> HandleAsync(ExecuteCostBatchSegment command, CancellationToken cancellationToken = default)
    {
        var applied = await observations.ObserveAsync(new TaskExecutionDescriptor("costing.batch.segment", command.BatchId, command.Epoch), async () =>
        {
            var entry = await database.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == command.BatchId, cancellationToken).ConfigureAwait(false);
            return new BackgroundExecutionInput<CostBatchEntry?>(entry, entry?.ExecutionOrigin);
        }, async entry =>
        {
            if (entry is null || entry.State != "Running" || entry.Epoch != command.Epoch) { return false; }
            var sequences = await database.BatchRows.AsNoTracking().Where(x => x.BatchId == command.BatchId && x.Sequence > entry.Checkpoint)
                .OrderBy(x => x.Sequence).Take(batches.SegmentSize).Select(x => x.Sequence).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var sequence in sequences)
            {
                if (!await ApplyRowAsync(command.BatchId, command.Epoch, sequence, cancellationToken).ConfigureAwait(false)) { return false; }
            }
            return true;
        }, static committed => committed ? BackgroundExecutionOutcome.Completed : BackgroundExecutionOutcome.LeaseLost, cancellationToken).ConfigureAwait(false);
        return Result.Success(applied);
    }

    private async Task<bool> ApplyRowAsync(Guid batchId, long epoch, int sequence, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(batches.RowTimeout);
        token = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        // Budgets are transaction local and shorter than the total deadline, including lock waits.
        await ConfigureTransactionAsync(token).ConfigureAwait(false);
        var entry = await LockAsync(batchId, token).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(token).ConfigureAwait(false);
        if (!Owns(entry, epoch, now)) { return false; }
        var originalDeadline = entry!.LeaseUntil!.Value;
        if (originalDeadline - now < options.LeaseDuration / 2)
        {
            var proposed = now + options.LeaseDuration;
            entry.LeaseUntil = proposed < entry.MaxLeaseUntil ? proposed : entry.MaxLeaseUntil!.Value;
        }
        if (sequence <= entry.Checkpoint) { return true; }
        if (sequence != entry.Checkpoint + 1) { return false; }
        var row = await database.BatchRows.SingleAsync(x => x.BatchId == batchId && x.Sequence == sequence, token).ConfigureAwait(false);
        if (row.Outcome == "Pending")
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-request/" + row.TaskId}, 0))", token).ConfigureAwait(false);
            if (await database.Tasks.AnyAsync(x => x.TaskId == row.TaskId, token).ConfigureAwait(false)
                || await database.ScheduleReceipts.AnyAsync(x => x.OccurrenceId == row.TaskId && x.Decision == "Accepted", token).ConfigureAwait(false))
            {
                // A conflicting namespace is an infrastructure fault, never a row business rejection.
                throw new InvalidOperationException("成本批次子任务身份已被占用。");
            }
            // A later schedule may persist a rejection for this reserved identity; it owns no task.
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-item/" + row.ItemId}, 0))", token).ConfigureAwait(false);
            var id = new CostId(row.ItemId);
            var sheet = await database.Sheets.FindAsync([id], token).ConfigureAwait(false);
            if ((sheet?.Version ?? 0) != row.ExpectedVersion)
            {
                row.Outcome = "Rejected";
                row.ErrorCode = "costing.version_conflict";
                entry.Rejected++;
            }
            else
            {
                var unchanged = sheet is not null && sheet.PurchaseCost == row.PurchaseCost && sheet.FreightCost == row.FreightCost;
                if (sheet is null)
                {
                    sheet = CostSheet.Create(id, row.PurchaseCost, row.FreightCost).Value;
                    database.Sheets.Add(sheet);
                }
                else { _ = sheet.UpdateCost(row.PurchaseCost, row.FreightCost); }
                database.Tasks.Add(new CostCalculationEntry
                {
                    TaskId = row.TaskId!.Value,
                    Origin = "batch",
                    ItemId = id,
                    ExpectedVersion = row.ExpectedVersion,
                    PurchaseCost = row.PurchaseCost,
                    FreightCost = row.FreightCost,
                    InputRevision = sheet.InputRevision,
                    ExecutionOrigin = execution.Capture() ?? entry.ExecutionOrigin,
                    CreatedAt = now,
                    AvailableAt = now,
                });
                row.Outcome = unchanged ? "Unchanged" : "Imported";
                if (unchanged) { entry.Unchanged++; } else { entry.Imported++; }
            }
        }
        entry.Checkpoint = sequence;
        if (sequence == entry.TotalRows)
        {
            entry.State = entry.Rejected == 0 ? "Completed" : "CompletedWithErrors";
            await FinishAttemptAsync(entry, entry.State, now, null, token).ConfigureAwait(false);
        }
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        now = await database.DatabaseTimeAsync(token).ConfigureAwait(false);
        // The parent is locked throughout; a final-state write cannot prove the old lease valid.
        if (entry.Epoch != epoch || originalDeadline <= now || entry.MaxLeaseUntil <= now) { return false; }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    public async Task<Result<bool>> HandleAsync(FailCostBatch command, CancellationToken cancellationToken = default)
    {
        using var budget = CreateBudget(cancellationToken);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = await LockAsync(command.BatchId, cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(entry, command.Epoch, now)) { return Result.Success(false); }
        entry!.State = entry.Attempts >= options.MaxAttempts ? "Failed" : "Retry";
        entry.ErrorCode = "costing.batch.execution_failed";
        entry.AvailableAt = now + options.RetryDelay * entry.Attempts;
        await FinishAttemptAsync(entry, "Failed", now, entry.ErrorCode, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (entry.LeaseUntil <= now || entry.MaxLeaseUntil <= now) { return Result.Success(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(true);
    }

    private async Task<CostBatchEntry?> LockAsync(Guid batchId, CancellationToken token) =>
        (await database.Batches.FromSqlInterpolated($"SELECT * FROM costing.batches WHERE \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(token).ConfigureAwait(false)).SingleOrDefault();
    private CancellationTokenSource CreateBudget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(batches.RowTimeout);
        return budget;
    }
    private Task<int> ConfigureTransactionAsync(CancellationToken token) =>
        database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1000ms'; SET LOCAL statement_timeout = '1500ms'", token);
    private static bool Owns(CostBatchEntry? entry, long epoch, DateTimeOffset now) =>
        entry is { State: "Running" } && entry.Epoch == epoch && entry.LeaseUntil > now && entry.MaxLeaseUntil > now;
    private async Task FinishAttemptAsync(CostBatchEntry entry, string outcome, DateTimeOffset now, string? error, CancellationToken token)
    {
        var attempt = await database.BatchAttempts.SingleAsync(x => x.BatchId == entry.BatchId && x.Epoch == entry.Epoch, token).ConfigureAwait(false);
        attempt.FinishedAt = now;
        attempt.Outcome = outcome;
        attempt.ErrorCode = error;
    }
}
