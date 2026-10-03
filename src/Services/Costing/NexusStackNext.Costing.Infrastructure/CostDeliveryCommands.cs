using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostDeliveryCommands(CostingDbContext database) : IQueryHandler<GetCostDelivery, CostDeliveryStatus>, ICommandHandler<RetryCostDelivery, CostDeliveryStatus>
{
    public async Task<Result<CostDeliveryStatus>> HandleAsync(GetCostDelivery query, CancellationToken cancellationToken = default)
    {
        var entry = await database.Outbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == query.TaskId && x.EventName == CostCalculatedV1.Name, cancellationToken).ConfigureAwait(false);
        return entry is null ? Result.Failure<CostDeliveryStatus>(new Error("costing.not_found", "尚无可投递结果。")) : Result.Success(ToStatus(entry));
    }

    public async Task<Result<CostDeliveryStatus>> HandleAsync(RetryCostDelivery command, CancellationToken cancellationToken = default)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = (await database.Outbox.FromSqlInterpolated(
            $"SELECT * FROM costing.outbox WHERE \"Id\" = {command.TaskId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        var retried = entry?.EventName == CostCalculatedV1.Name ? entry.RetryDelivery(command.ExpectedDeadLetteredAt) : null;
        if (retried is null)
        {
            return Result.Failure<CostDeliveryStatus>(new Error("costing.delivery_conflict", "投递状态已经改变。"));
        }
        database.Entry(entry!).CurrentValues.SetValues(retried);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(ToStatus(retried));
    }

    private static CostDeliveryStatus ToStatus(OutboxEntry entry) => new(entry.Id,
        entry.IsDelivered ? "Delivered" : entry.IsDeadLettered ? "DeadLettered" : "Pending",
        entry.AttemptCount, entry.NextAttemptAt, entry.DeadLetteredAt);
}
