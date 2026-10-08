using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostBatchQueries(CostingDbContext database) : IQueryHandler<ListCostBatches, CostBatchPage>,
    IQueryHandler<ListCostBatchAttempts, CostBatchAttemptPage>
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    { "Pending", "Running", "Retry", "Failed", "Cancelled", "Completed", "CompletedWithErrors" };

    public async Task<Result<CostBatchPage>> HandleAsync(ListCostBatches query, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(query.Page, query.Limit) || query.State is not null && !States.Contains(query.State))
        {
            return Result.Failure<CostBatchPage>(CostBatchAcceptance.InvalidInput);
        }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var batches = database.Batches.AsNoTracking();
        if (query.State is not null) { batches = batches.Where(x => x.State == query.State); }
        var total = await batches.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await batches.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.BatchId).Skip((query.Page - 1) * query.Limit)
            .Take(query.Limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new CostBatchPage(page.Select(x => x.ToStatus()).ToArray(), total));
    }

    internal static bool ValidPage(int page, int limit) => page >= 1 && limit is >= 1 and <= 200 && ((long)page - 1) * limit <= 100000;

    public async Task<Result<CostBatchAttemptPage>> HandleAsync(ListCostBatchAttempts query, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(query.Page, query.Limit)) { return Result.Failure<CostBatchAttemptPage>(CostBatchAcceptance.InvalidInput); }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        if (!await database.Batches.AnyAsync(x => x.BatchId == query.BatchId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<CostBatchAttemptPage>(CostBatchAcceptance.NotFound);
        }
        var attempts = database.BatchAttempts.AsNoTracking().Where(x => x.BatchId == query.BatchId);
        var total = await attempts.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await attempts.OrderBy(x => x.Epoch).Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .Select(x => new CostingAttempt(x.Epoch, x.StartedAt, x.FinishedAt, x.Outcome, x.ErrorCode)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new CostBatchAttemptPage(items, total));
    }
}
