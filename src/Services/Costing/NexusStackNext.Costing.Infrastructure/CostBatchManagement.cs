using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed partial class CostBatchExecution : ICommandHandler<CancelCostBatch, CostBatchStatus>, ICommandHandler<RetryCostBatch, CostBatchStatus>,
    ICommandHandler<RenewCostBatch, CostBatchLease>
{
    public async Task<Result<CostBatchLease>> HandleAsync(RenewCostBatch command, CancellationToken cancellationToken = default)
    {
        using var budget = CreateBudget(cancellationToken);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = await LockAsync(command.BatchId, cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(entry, command.Epoch, now))
        {
            return Result.Failure<CostBatchLease>(new Error("costing.batch.renew_conflict", "批次执行权已经失效。"));
        }
        var oldDeadline = entry!.LeaseUntil!.Value;
        var proposed = now + options.LeaseDuration;
        var deadline = proposed < entry.MaxLeaseUntil ? proposed : entry.MaxLeaseUntil!.Value;
        if (deadline <= oldDeadline)
        {
            return Result.Failure<CostBatchLease>(new Error("costing.batch.renew_conflict", "本次领取已达到租约总期限。"));
        }
        entry.LeaseUntil = deadline;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (oldDeadline <= await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<CostBatchLease>(new Error("costing.batch.renew_conflict", "原执行权在续租提交前已失效。"));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new CostBatchLease(entry.BatchId, entry.Epoch, deadline));
    }

    public async Task<Result<CostBatchStatus>> HandleAsync(RetryCostBatch command, CancellationToken cancellationToken = default)
    {
        using var budget = CreateBudget(cancellationToken);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = await LockAsync(command.BatchId, cancellationToken).ConfigureAwait(false);
        if (entry is null) { return Result.Failure<CostBatchStatus>(CostBatchAcceptance.NotFound); }
        if (entry.State != "Failed" || entry.Epoch != command.ExpectedEpoch || entry.Epoch == long.MaxValue)
        {
            return Result.Failure<CostBatchStatus>(new Error("costing.batch.retry_conflict", "批次状态或执行代次已经改变。"));
        }
        entry.State = "Retry";
        entry.Attempts = 0;
        entry.ErrorCode = null;
        entry.LeaseUntil = null;
        entry.MaxLeaseUntil = null;
        entry.AvailableAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(entry.ToStatus());
    }

    public async Task<Result<CostBatchStatus>> HandleAsync(CancelCostBatch command, CancellationToken cancellationToken = default)
    {
        using var budget = CreateBudget(cancellationToken);
        cancellationToken = budget.Token;
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = await LockAsync(command.BatchId, cancellationToken).ConfigureAwait(false);
        if (entry is null) { return Result.Failure<CostBatchStatus>(CostBatchAcceptance.NotFound); }
        if (entry.Epoch != command.ExpectedEpoch || entry.State is not ("Pending" or "Running" or "Retry" or "Cancelled"))
        {
            return Result.Failure<CostBatchStatus>(new Error("costing.batch.cancel_conflict", "批次状态或执行代次已经改变。"));
        }
        if (entry.State != "Cancelled")
        {
            if (entry.State == "Running")
            {
                await FinishAttemptAsync(entry, "Cancelled", await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false);
            }
            entry.State = "Cancelled";
            entry.LeaseUntil = null;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(entry.ToStatus());
    }
}
