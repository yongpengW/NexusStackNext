using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;

namespace NexusStackNext.Platform.Infrastructure;

internal sealed class EfSettingAuditDelivery(PlatformDbContext context) : ISettingAuditDelivery
{
    public async Task<IReadOnlyList<SettingAuditDelivery>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var query = context.Outbox.AsNoTracking().Where(entry => entry.EventName == SettingCommittedV1.Name);
        query = state switch
        {
            "Pending" => query.Where(entry => entry.DeliveredAt == null && entry.DeadLetteredAt == null),
            "Delivered" => query.Where(entry => entry.DeliveredAt != null),
            "DeadLettered" => query.Where(entry => entry.DeliveredAt == null && entry.DeadLetteredAt != null),
            _ => throw new ArgumentException("未知投递状态。", nameof(state)),
        };
        var entries = await query.OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id).Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return entries.Select(SettingAuditDelivery.From).ToArray();
    }

    public Task<Result<SettingAuditDelivery>> RetryAsync(Guid messageId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var entry = (await context.Outbox.FromSqlInterpolated($"SELECT * FROM platform.outbox WHERE \"Id\" = {messageId} FOR UPDATE")
                .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (entry is null || entry.EventName != SettingCommittedV1.Name || entry.IsDelivered || entry.DeadLetteredAt != expectedDeadLetteredAt)
            {
                return Result.Failure<SettingAuditDelivery>(new Error("platform.delivery_conflict", "投递状态已经改变，请重新读取。"));
            }
            var retry = entry with { AttemptCount = 0, NextAttemptAt = null, DeadLetteredAt = null, LastFailure = null };
            context.Entry(entry).CurrentValues.SetValues(retry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(SettingAuditDelivery.From(retry));
        });
}
