using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingFeeCommands(PricingDbContext database, IExecutionContext execution) : ICommandHandler<UpdatePricingFee, RecalculationStatus>
{
    public async Task<Result<RecalculationStatus>> HandleAsync(UpdatePricingFee command, CancellationToken cancellationToken = default)
    {
        if (command.ItemId == Guid.Empty || command.RequestId == Guid.Empty || command.ExpectedVersion <= 0
            || !PriceQuote.IsValidInput(0, command.FeeRate) || command.DelaySeconds is < 0 or > 2_592_000)
        {
            return Result.Failure<RecalculationStatus>(new Error("pricing.invalid_input", "标识、版本或费率无效。"));
        }
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-request/" + command.RequestId}, 0))", cancellationToken).ConfigureAwait(false);
        var existing = await database.Tasks.Include(x => x.History).SingleOrDefaultAsync(x => x.TaskId == command.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Origin == "fee" && existing.ItemId.Value == command.ItemId
                && existing.ExpectedVersion == command.ExpectedVersion && existing.FeeRate == command.FeeRate && existing.DelaySeconds == command.DelaySeconds
                ? Result.Success(existing.ToStatus()) : Result.Failure<RecalculationStatus>(new Error("pricing.request_conflict", "请求标识已用于不同内容。"));
        }
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-item/" + command.ItemId}, 0))", cancellationToken).ConfigureAwait(false);
        var id = new PriceId(command.ItemId);
        var quote = await database.Quotes.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (quote is null) { return Result.Failure<RecalculationStatus>(new Error("pricing.not_found", "定价对象不存在。")); }
        if (quote.Version != command.ExpectedVersion) { return Result.Failure<RecalculationStatus>(new Error("pricing.version_conflict", "定价对象已被修改。")); }
        _ = quote.UpdateCost(quote.Cost, command.FeeRate);
        var acceptedAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var task = new RecalculationEntry
        {
            TaskId = command.RequestId,
            ExecutionOrigin = execution.Capture(),
            ItemId = id,
            ExpectedVersion = command.ExpectedVersion,
            Origin = "fee",
            Cost = quote.Cost,
            FeeRate = quote.FeeRate,
            InputRevision = quote.InputRevision,
            DelaySeconds = command.DelaySeconds,
            CreatedAt = acceptedAt,
            AvailableAt = acceptedAt.AddSeconds(command.DelaySeconds),
        };
        database.Tasks.Add(task);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (PricingFactCapacityFailure.IsExhausted(error))
        {
            database.ChangeTracker.Clear();
            return Result.Failure<RecalculationStatus>(PricingErrors.AuditCapacityExceeded);
        }
        return Result.Success(task.ToStatus());
    }
}
