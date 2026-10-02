using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Tasks;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExecution(PricingDbContext database, PricingTaskOptions options) : ICommandHandler<ClaimPricingWork, PricingWorkLease?>,
    ICommandHandler<CompletePricingWork, bool>, ICommandHandler<FailPricingWork, bool>, ICommandHandler<RetryPricingWork, RecalculationStatus>
{
    private readonly PostgresTaskExecution<RecalculationEntry> _execution = new(database, "pricing", options);

    public async Task<Result<bool>> HandleAsync(FailPricingWork command, CancellationToken cancellationToken = default) =>
        Result.Success(await _execution.FailAsync(command.TaskId, command.Epoch, cancellationToken).ConfigureAwait(false));

    public async Task<Result<RecalculationStatus>> HandleAsync(RetryPricingWork command, CancellationToken cancellationToken = default)
    {
        var result = await _execution.RetryAsync(command.TaskId, command.ExpectedEpoch, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(result.Value.ToStatus()) : Result.Failure<RecalculationStatus>(result.Error);
    }

    public async Task<Result<PricingWorkLease?>> HandleAsync(ClaimPricingWork command, CancellationToken cancellationToken = default)
    {
        var task = await _execution.ClaimAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task is null ? null : new PricingWorkLease(task.TaskId, task.Epoch, task.LeaseUntil!.Value));
    }
    public async Task<Result<bool>> HandleAsync(CompletePricingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        var input = await database.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == command.TaskId, cancellationToken).ConfigureAwait(false);
        if (input is null) { return Result.Success(false); }
        // 耗时计算的缝在短事务之外；首轮只有一个确定的演示公式。
        var price = PriceQuote.Calculate(input.Cost, input.FeeRate);
        return Result.Success(await _execution.CompleteAsync(command.TaskId, command.Epoch, async (task, token) =>
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-item/" + task.ItemId.Value}, 0))", token).ConfigureAwait(false);
            var quote = await database.Quotes.SingleAsync(x => x.Id == task.ItemId, token).ConfigureAwait(false);
            task.State = quote.ApplyCalculation(task.InputRevision, price).IsSuccess ? "Succeeded" : "Superseded";
            return task.State;
        }, cancellationToken).ConfigureAwait(false));
    }
}
