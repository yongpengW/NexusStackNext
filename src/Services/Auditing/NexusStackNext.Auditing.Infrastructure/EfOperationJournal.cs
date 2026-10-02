using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class EfOperationJournal(IDbContextFactory<OperationJournalDbContext> contexts) : IOperationJournal
{
    public async Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var valid = OperationObservationIngestion.Validate(observation);
        if (valid.IsFailure) { return valid; }
        var record = OutboxEntry.From(observation, new SystemTextJsonIntegrationEventSerializer());
        // 每次调用独立上下文；开始与结束记录不能借用请求的业务事务。
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"operation-journal-message/" + record.Id}, 0))", token).ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"operation-journal-phase/" + observation.Source + "/" + observation.OperationId + "/" + observation.Phase}, 0))", token).ConfigureAwait(false);
            var existing = await context.Outbox.AsNoTracking().SingleOrDefaultAsync(item => item.Id == record.Id, token).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.EventName == record.EventName && existing.Payload == record.Payload
                    ? Result.Success() : Result.Failure(OperationJournalErrors.IdentityConflict);
            }
            if (await context.Outbox.AnyAsync(item =>
                EF.Property<string>(item, OperationJournalDbContext.SourceProperty) == observation.Source
                && EF.Property<Guid>(item, OperationJournalDbContext.OperationIdProperty) == observation.OperationId
                && EF.Property<string>(item, OperationJournalDbContext.PhaseProperty) == observation.Phase, token).ConfigureAwait(false))
            {
                return Result.Failure(OperationJournalErrors.IdentityConflict);
            }
            var entry = context.Outbox.Add(record);
            entry.Property<string>(OperationJournalDbContext.SourceProperty).CurrentValue = observation.Source;
            entry.Property<Guid>(OperationJournalDbContext.OperationIdProperty).CurrentValue = observation.OperationId;
            entry.Property<string>(OperationJournalDbContext.PhaseProperty).CurrentValue = observation.Phase;
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal static class OperationJournalErrors
{
    internal static readonly Error IdentityConflict = new("operation_journal.identity_conflict", "操作日志身份已用于不同内容。");
}
