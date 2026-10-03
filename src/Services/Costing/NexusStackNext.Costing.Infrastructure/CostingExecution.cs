using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Tasks;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostingExecution(CostingDbContext database, CostingTaskOptions options) : ICommandHandler<ClaimCostingWork, CostingWorkLease?>,
    ICommandHandler<CompleteCostingWork, bool>, ICommandHandler<FailCostingWork, bool>, ICommandHandler<RetryCostingWork, CostCalculationStatus>,
    ICommandHandler<CancelCostingWork, CostCalculationStatus>, ICommandHandler<RenewCostingWork, CostingWorkLease>
{
    private readonly PostgresTaskExecution<CostCalculationEntry> _execution = new(database, "costing", options);

    public async Task<Result<CostingWorkLease>> HandleAsync(RenewCostingWork command, CancellationToken cancellationToken = default)
    {
        var result = await _execution.RenewAsync(command.TaskId, command.Epoch, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(new CostingWorkLease(result.Value.TaskId, result.Value.Epoch, result.Value.LeaseUntil!.Value))
            : Result.Failure<CostingWorkLease>(result.Error);
    }

    public async Task<Result<CostCalculationStatus>> HandleAsync(CancelCostingWork command, CancellationToken cancellationToken = default)
    {
        var result = await _execution.CancelAsync(command.TaskId, command.ExpectedEpoch, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(result.Value.ToStatus()) : Result.Failure<CostCalculationStatus>(result.Error);
    }

    public async Task<Result<bool>> HandleAsync(FailCostingWork command, CancellationToken cancellationToken = default) =>
        Result.Success(await _execution.FailAsync(command.TaskId, command.Epoch, cancellationToken).ConfigureAwait(false));

    public async Task<Result<CostCalculationStatus>> HandleAsync(RetryCostingWork command, CancellationToken cancellationToken = default)
    {
        var result = await _execution.RetryAsync(command.TaskId, command.ExpectedEpoch, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(result.Value.ToStatus()) : Result.Failure<CostCalculationStatus>(result.Error);
    }

    public async Task<Result<CostingWorkLease?>> HandleAsync(ClaimCostingWork command, CancellationToken cancellationToken = default)
    {
        var task = await _execution.ClaimAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task is null ? null : new CostingWorkLease(task.TaskId, task.Epoch, task.LeaseUntil!.Value));
    }
    public async Task<Result<bool>> HandleAsync(CompleteCostingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        var input = await database.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == command.TaskId, cancellationToken).ConfigureAwait(false);
        if (input is null) { return Result.Success(false); }
        // 耗时计算的缝在短事务之外；首轮只有一个确定的演示公式。
        var unitCost = CostSheet.Calculate(input.PurchaseCost, input.FreightCost);
        return Result.Success(await _execution.CompleteAsync(command.TaskId, command.Epoch, async (task, token) =>
        {
            var now = await database.DatabaseTimeAsync(token).ConfigureAwait(false);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-item/" + task.ItemId.Value}, 0))", token).ConfigureAwait(false);
            var sheet = await database.Sheets.SingleAsync(x => x.Id == task.ItemId, token).ConfigureAwait(false);
            var outcome = sheet.ApplyCalculation(task.InputRevision, unitCost).IsSuccess ? TaskCompletion.Succeeded : TaskCompletion.Superseded;
            if (outcome == TaskCompletion.Succeeded)
            {
                database.Outbox.Add(OutboxEntry.From(new CostCalculatedV1
                {
                    EventId = task.TaskId,
                    OccurredAt = now,
                    ItemId = task.ItemId.Value,
                    CostRevision = task.InputRevision,
                    UnitCost = unitCost,
                }, new SystemTextJsonIntegrationEventSerializer()));
            }
            return outcome;
        }, cancellationToken).ConfigureAwait(false));
    }
}
