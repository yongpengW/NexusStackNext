using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExecution(PricingDbContext database, PricingTaskOptions options) : ICommandHandler<ClaimPricingWork, PricingWorkLease?>,
    ICommandHandler<CompletePricingWork, bool>, ICommandHandler<FailPricingWork, bool>, ICommandHandler<RetryPricingWork, RecalculationStatus>
{
    public async Task<Result<bool>> HandleAsync(FailPricingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(task, command.Epoch, now)) { return Result.Success(false); }
        task!.State = task.Attempts >= options.MaxAttempts ? "Failed" : "Retry";
        task.ErrorCode = "pricing.calculation_failed";
        task.AvailableAt = now + options.RetryDelay * task.Attempts;
        await FinishAttemptAsync(task, "Failed", now, task.ErrorCode, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (task.LeaseUntil <= await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false)) { return Result.Success(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(true);
    }

    public async Task<Result<RecalculationStatus>> HandleAsync(RetryPricingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);
        if (task is null) { return Result.Failure<RecalculationStatus>(new Error("pricing.not_found", "任务不存在。")); }
        if (task.State != "Failed" || task.Epoch != command.ExpectedEpoch)
        {
            return Result.Failure<RecalculationStatus>(new Error("pricing.retry_conflict", "任务状态或执行代次已经改变。"));
        }
        task.State = "Retry";
        task.Attempts = 0;
        task.ErrorCode = null;
        task.LeaseUntil = null;
        task.AvailableAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await database.Entry(task).Collection(x => x.History).LoadAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task.ToStatus());
    }

    public async Task<Result<PricingWorkLease?>> HandleAsync(ClaimPricingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await database.Tasks.FromSqlRaw("""
            SELECT * FROM pricing.tasks
            WHERE ("State" IN ('Pending', 'Retry') AND "AvailableAt" <= clock_timestamp())
               OR ("State" = 'Running' AND "LeaseUntil" <= clock_timestamp())
            ORDER BY "AvailableAt", "TaskId" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var task = candidates.SingleOrDefault();
        if (task is null) { return Result.Success<PricingWorkLease?>(null); }
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (task.State == "Running")
        {
            await FinishAttemptAsync(task, "Expired", now, "pricing.lease_expired", cancellationToken).ConfigureAwait(false);
        }
        if (task.Attempts >= options.MaxAttempts)
        {
            task.State = "Failed";
            task.ErrorCode = "pricing.attempts_exhausted";
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success<PricingWorkLease?>(null);
        }
        task.State = "Running";
        task.Epoch++;
        task.Attempts++;
        task.ErrorCode = null;
        task.LeaseUntil = now + options.LeaseDuration;
        database.Attempts.Add(new AttemptEntry { TaskId = task.TaskId, Epoch = task.Epoch, StartedAt = now });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<PricingWorkLease?>(new PricingWorkLease(task.TaskId, task.Epoch, task.LeaseUntil.Value));
    }

    public async Task<Result<bool>> HandleAsync(CompletePricingWork command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        var input = await database.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == command.TaskId, cancellationToken).ConfigureAwait(false);
        if (input is null) { return Result.Success(false); }
        // 耗时计算的缝在短事务之外；首轮只有一个确定的演示公式。
        var price = PriceQuote.Calculate(input.Cost, input.FeeRate);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await LockTaskAsync(command.TaskId, cancellationToken).ConfigureAwait(false);
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        if (!Owns(task, command.Epoch, now))
        {
            return Result.Success(false);
        }
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-item/" + task!.ItemId.Value}, 0))", cancellationToken).ConfigureAwait(false);
        var quote = await database.Quotes.SingleAsync(x => x.Id == task.ItemId, cancellationToken).ConfigureAwait(false);
        task.State = quote.ApplyCalculation(task.InputRevision, price).IsSuccess ? "Succeeded" : "Superseded";
        await FinishAttemptAsync(task, task.State, now, null, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (task.LeaseUntil <= await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(true);
    }

    private async Task<RecalculationEntry?> LockTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var tasks = await database.Tasks.FromSqlInterpolated(
            $"SELECT * FROM pricing.tasks WHERE \"TaskId\" = {taskId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return tasks.SingleOrDefault();
    }

    private static bool Owns(RecalculationEntry? task, long epoch, DateTimeOffset now) =>
        task is { State: "Running" } && task.Epoch == epoch && task.LeaseUntil > now;

    private async Task FinishAttemptAsync(RecalculationEntry task, string outcome, DateTimeOffset now, string? errorCode, CancellationToken cancellationToken)
    {
        var attempt = await database.Attempts.SingleAsync(x => x.TaskId == task.TaskId && x.Epoch == task.Epoch, cancellationToken).ConfigureAwait(false);
        attempt.Outcome = outcome;
        attempt.FinishedAt = now;
        attempt.ErrorCode = errorCode;
    }
}
