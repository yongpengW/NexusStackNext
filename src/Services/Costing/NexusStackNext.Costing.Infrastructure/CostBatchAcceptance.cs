using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostBatchAcceptance(CostingDbContext database, IExecutionContext execution, CostingBatchOptions options) : ICommandHandler<AcceptCostBatch, CostBatchStatus>,
    IQueryHandler<GetCostBatch, CostBatchStatus>, IQueryHandler<ListCostBatchRows, CostBatchRowPage>
{
    public async Task<Result<CostBatchStatus>> HandleAsync(AcceptCostBatch command, CancellationToken cancellationToken = default)
    {
        // Copy once: caller-owned collections cannot change the identity after validation.
        var rows = command.Rows?.Take(5001).ToArray() ?? [];
        if (CostBatchValidation.Check(command with { Rows = rows }).ErrorCount != 0)
        {
            return Result.Failure<CostBatchStatus>(InvalidInput);
        }
        var hash = ContentHash(rows);
        var winners = rows.Select((x, index) => (x.ItemId, Sequence: index + 1)).GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.Last().Sequence);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.AcceptanceTimeout);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1000ms'; SET LOCAL statement_timeout = '5000ms'", cancellationToken).ConfigureAwait(false);
        // Identical large submissions can outlive one short lock wait. Poll without holding any
        // business lock, under the same finite acceptance budget, then reconcile the original record.
        while (!await database.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock(hashtextextended({"costing-batch/" + command.BatchRequestId}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        var existing = await database.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == command.BatchRequestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.ContentHash == hash ? Result.Success(existing.ToStatus())
                : Result.Failure<CostBatchStatus>(new Error("costing.batch.request_conflict", "批次身份已用于其他输入。"));
        }
        var taskIds = winners.Values.Select(sequence => RowTaskId(command.BatchRequestId, sequence)).ToArray();
        // Reserve child identities under the same lock namespace as manual and scheduled requests.
        // One round trip for the bounded array, not one network command per row.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('costing-request/' || reserved.id::text, 0))
            FROM (SELECT id FROM unnest({taskIds}) AS id ORDER BY id) AS reserved
            """, cancellationToken).ConfigureAwait(false);
        if (await database.Tasks.AnyAsync(x => taskIds.Contains(x.TaskId), cancellationToken).ConfigureAwait(false)
            || await database.ScheduleReceipts.AnyAsync(x => taskIds.Contains(x.OccurrenceId), cancellationToken).ConfigureAwait(false)
            || await database.BatchRows.AnyAsync(x => x.TaskId.HasValue && taskIds.Contains(x.TaskId.Value), cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<CostBatchStatus>(new Error("costing.batch.request_conflict", "批次子任务身份已被占用。"));
        }
        var batch = new CostBatchEntry
        {
            BatchId = command.BatchRequestId,
            CreatedAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false),
            ContentHash = hash,
            TotalRows = rows.Length,
            DuplicateSuperseded = rows.Length - winners.Count,
            ExecutionOrigin = execution.Capture(),
        };
        batch.AvailableAt = batch.CreatedAt;
        database.Batches.Add(batch);
        for (var index = 0; index < rows.Length; index++)
        {
            var input = rows[index];
            var sequence = index + 1;
            var winner = winners[input.ItemId];
            database.BatchRows.Add(new CostBatchRowEntry
            {
                BatchId = batch.BatchId,
                Sequence = sequence,
                SourceRow = input.SourceRow,
                ItemId = input.ItemId,
                ExpectedVersion = input.ExpectedVersion,
                PurchaseCost = input.PurchaseCost,
                FreightCost = input.FreightCost,
                EffectiveSequence = winner,
                TaskId = winner == sequence ? RowTaskId(batch.BatchId, sequence) : null,
                Outcome = winner == sequence ? "Pending" : "DuplicateSuperseded",
            });
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(batch.ToStatus());
    }

    public async Task<Result<CostBatchStatus>> HandleAsync(GetCostBatch query, CancellationToken cancellationToken = default)
    {
        var batch = await database.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == query.BatchId, cancellationToken).ConfigureAwait(false);
        return batch is null ? Result.Failure<CostBatchStatus>(NotFound) : Result.Success(batch.ToStatus());
    }

    public async Task<Result<CostBatchRowPage>> HandleAsync(ListCostBatchRows query, CancellationToken cancellationToken = default)
    {
        if (query.Page < 1 || query.Limit is < 1 or > 200 || ((long)query.Page - 1) * query.Limit > 100000)
        {
            return Result.Failure<CostBatchRowPage>(InvalidInput);
        }
        var total = await database.Batches.Where(x => x.BatchId == query.BatchId).Select(x => (int?)x.TotalRows).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (total is null) { return Result.Failure<CostBatchRowPage>(NotFound); }
        var rows = await database.BatchRows.AsNoTracking().Where(x => x.BatchId == query.BatchId).OrderBy(x => x.Sequence)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .Select(x => new CostBatchRowStatus(x.Sequence, x.SourceRow, x.ItemId, x.Outcome, x.EffectiveSequence,
                x.TaskId, x.ErrorCode, database.Tasks.Where(task => task.TaskId == x.TaskId).Select(task => task.State).FirstOrDefault()))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new CostBatchRowPage(rows, total.Value));
    }

    private static string ContentHash(IReadOnlyList<CostBatchInput> rows)
    {
        var canonical = new StringBuilder("costing-batch/v1:last-wins\n");
        foreach (var row in rows)
        {
            canonical.Append(CultureInfo.InvariantCulture, $"{row.SourceRow}|{row.ItemId:D}|{row.ExpectedVersion}|{row.PurchaseCost:G29}|{row.FreightCost:G29}\n");
        }
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
    private static Guid RowTaskId(Guid batchId, int sequence) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"costing-batch-task/v1/{batchId:D}/{sequence}"))).AsSpan(0, 16));
    internal static readonly Error InvalidInput = new("costing.batch.invalid_input", "批次身份、行数或成本输入无效。");
    internal static readonly Error NotFound = new("costing.batch.not_found", "批次不存在。");
}
