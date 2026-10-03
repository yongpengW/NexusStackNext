using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostingTaskQueries(CostingDbContext database) : IQueryHandler<ListCostCalculations, CostCalculationPage>
{
    public async Task<Result<CostCalculationPage>> HandleAsync(ListCostCalculations query, CancellationToken cancellationToken = default)
    {
        var offset = ((long)query.Page - 1) * query.Limit;
        if (query.Page <= 0 || query.Limit is < 1 or > 200 || offset > 100_000 || query.ItemId == Guid.Empty
            || query.State is not (null or "Pending" or "Running" or "Retry" or "Failed" or "Succeeded" or "Superseded" or "Cancelled"))
        {
            return Result.Failure<CostCalculationPage>(new Error("costing.invalid_query", "查询状态、对象或分页无效；每页最多 200 项，偏移最多 100000，请收窄筛选。"));
        }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var tasks = database.Tasks.AsNoTracking();
        if (query.State is not null) { tasks = tasks.Where(task => task.State == query.State); }
        if (query.ItemId is { } itemId)
        {
            var id = new CostId(itemId);
            tasks = tasks.Where(task => task.ItemId == id);
        }
        var total = await tasks.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await tasks.OrderBy(task => task.CreatedAt == null).ThenByDescending(task => task.CreatedAt).ThenBy(task => task.TaskId)
            .Skip((int)offset).Take(query.Limit)
            .Select(task => new CostCalculationSummary(task.TaskId, task.ItemId.Value, task.State, task.CreatedAt, task.AvailableAt,
                task.Epoch, task.Attempts, task.LeaseUntil, task.MaxLeaseUntil, task.ErrorCode))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new CostCalculationPage(items, total));
    }
}
