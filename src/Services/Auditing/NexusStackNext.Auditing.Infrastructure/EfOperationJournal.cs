using System.Text;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class EfOperationJournal(IDbContextFactory<OperationJournalDbContext> contexts, OperationJournalCapacityOptions capacity,
    OperationJournalCleanupOptions cleanup, IClock clock)
    : IOperationJournal, IOperationJournalMaintenance
{
    public async Task<Result<OperationJournalDelivery>> GetDeliveryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var view = await DeliveryViews(context.Outbox.Where(item => item.Id == messageId)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return view is null ? Result.Failure<OperationJournalDelivery>(OperationJournalDeliveryErrors.NotFound) : Result.Success(view);
    }

    private static IQueryable<OperationJournalDelivery> DeliveryViews(IQueryable<OutboxEntry> entries) => entries.Select(item =>
        new OperationJournalDelivery(item.Id, EF.Property<string>(item, OperationJournalDbContext.SourceProperty),
            EF.Property<Guid>(item, OperationJournalDbContext.OperationIdProperty), EF.Property<string>(item, OperationJournalDbContext.PhaseProperty),
            item.DeliveredAt != null ? "Delivered" : item.DeadLetteredAt != null ? "DeadLettered" : "Pending",
            item.AttemptCount, item.NextAttemptAt, item.DeadLetteredAt, item.DeliveredAt,
            item.RetryRevision, item.LastFailure == null ? null : "operation_journal.delivery_failed"));
    public async Task<Result<OperationJournalDeadLetterPage>> QueryDeadLettersAsync(OperationJournalDeadLetterQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Validate().IsFailure) { return Result.Failure<OperationJournalDeadLetterPage>(OperationJournalDeliveryErrors.InvalidQuery); }
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var matching = context.Outbox.Where(item => item.DeadLetteredAt != null && item.DeliveredAt == null);
        if (query.Source is not null) { matching = matching.Where(item => EF.Property<string>(item, OperationJournalDbContext.SourceProperty) == query.Source); }
        var total = await matching.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await DeliveryViews(matching.OrderBy(item => item.DeadLetteredAt).ThenBy(item => item.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new OperationJournalDeadLetterPage(query.Page, query.Limit, total, items));
    }
    public async Task<Result<OperationJournalRecoveryReceipt>> RetryDeliveryAsync(OperationJournalRecoveryRequest request,
        OperationJournalRecoveryActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Validate(actor).IsFailure) { return Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.Invalid); }
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"operation-journal-recovery/" + request.RequestId}, 0))", token).ConfigureAwait(false);
            var existing = await context.Set<OperationJournalRecoveryRecord>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, token).ConfigureAwait(false);
            if (existing is not null)
            {
                var receipt = existing.Receipt();
                return receipt.Request == request && receipt.Actor == actor
                    ? Result.Success(receipt) : Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.RequestConflict);
            }
            // 恢复是低频管理操作；同库事务锁保护独立额度，不依赖普通观察的容量行。
            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended('operation-journal-recovery-capacity', 0))", token).ConfigureAwait(false);
            var row = (await context.Outbox.FromSqlInterpolated($"SELECT * FROM operation_journal.outbox WHERE \"Id\" = {request.MessageId} FOR UPDATE")
                .ToArrayAsync(token).ConfigureAwait(false)).SingleOrDefault();
            if (row is null) { return Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalDeliveryErrors.NotFound); }
            var retried = row.RetryDelivery(request.ExpectedDeadLetteredAt);
            if (retried is null || row.RetryRevision != request.ExpectedRetryRevision)
            {
                return Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalDeliveryErrors.Conflict);
            }
            if (await context.Set<OperationJournalRecoveryRecord>().LongCountAsync(token).ConfigureAwait(false) >= capacity.MaxRecoveryRecords)
            {
                return Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.CapacityExceeded);
            }
            var recoveredAt = clock.UtcNow;
            var committed = new OperationJournalRecoveryReceipt(request, actor,
                context.Entry(row).Property<string>(OperationJournalDbContext.SourceProperty).CurrentValue!, recoveredAt, retried.RetryRevision,
                recoveredAt + cleanup.RecoveryRetention);
            context.Set<OperationJournalRecoveryRecord>().Add(OperationJournalRecoveryRecord.From(committed));
            context.Entry(row).CurrentValues.SetValues(retried);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            // 首次响应也使用实际持久化精度，保证与丢响应后的重放凭据完全一致。
            var saved = await context.Set<OperationJournalRecoveryRecord>().AsNoTracking()
                .SingleAsync(item => item.RequestId == request.RequestId, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Result.Success(saved.Receipt());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<OperationJournalRecoveryReceipt>> GetRecoveryAsync(Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Set<OperationJournalRecoveryRecord>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken).ConfigureAwait(false);
        return row is null ? Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.NotFound) : Result.Success(row.Receipt());
    }
    public async Task<int> CleanupRecoveryRecordsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended('operation-journal-recovery-capacity', 0))", token).ConfigureAwait(false);
            var now = clock.UtcNow;
            var expired = await context.Set<OperationJournalRecoveryRecord>().Where(item => item.RetainUntil <= now)
                .OrderBy(item => item.RetainUntil).ThenBy(item => item.RequestId).Take(cleanup.BatchSize).ToArrayAsync(token).ConfigureAwait(false);
            context.RemoveRange(expired);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return expired.Length;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CleanupDeliveredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow - cleanup.DeliveredRetention;
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            // 和接纳共用额度锁；先锁额度再删已交付行，不与其他维护者重复释放。
            var capacityRows = await context.Set<OperationJournalCapacity>().FromSqlRaw(
                "SELECT * FROM operation_journal.capacity WHERE \"Id\" = 1 FOR UPDATE").AsNoTracking().ToArrayAsync(token).ConfigureAwait(false);
            if (capacityRows.Length != 1) { throw new InvalidOperationException("operation_journal.capacity_unavailable"); }
            var expired = await context.Outbox.FromSqlInterpolated($"""
                SELECT * FROM operation_journal.outbox
                WHERE "DeliveredAt" <= {cutoff} AND "DeadLetteredAt" IS NULL
                ORDER BY "DeliveredAt", "Id" LIMIT {cleanup.BatchSize} FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToArrayAsync(token).ConfigureAwait(false);
            if (expired.Length == 0) { return 0; }
            var bytes = expired.Sum(entry => (long)Encoding.UTF8.GetByteCount(entry.Payload));
            context.Outbox.RemoveRange(expired);
            var removed = await context.SaveChangesAsync(token).ConfigureAwait(false);
            var released = await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE operation_journal.capacity
                SET "RecordCount" = "RecordCount" - {removed}, "PayloadBytes" = "PayloadBytes" - {bytes}
                WHERE "Id" = 1 AND "RecordCount" >= {removed} AND "PayloadBytes" >= {bytes}
                """, token).ConfigureAwait(false);
            if (removed != expired.Length || released != 1) { throw new InvalidOperationException("operation_journal.capacity_inconsistent"); }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return removed;
        }, cancellationToken).ConfigureAwait(false);
    }
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
            var bytes = Encoding.UTF8.GetByteCount(record.Payload);
            if (bytes > capacity.MaxRecordPayloadBytes) { return Result.Failure(OperationJournalErrors.PayloadTooLarge); }
            var reserved = await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE operation_journal.capacity
                SET "RecordCount" = "RecordCount" + 1, "PayloadBytes" = "PayloadBytes" + {bytes}
                WHERE "Id" = 1 AND "RecordCount" < {capacity.MaxRecords}
                    AND "PayloadBytes" <= {capacity.MaxPayloadBytes - bytes}
                """, token).ConfigureAwait(false);
            if (reserved == 0) { return Result.Failure(OperationJournalErrors.CapacityExceeded); }
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
    internal static readonly Error PayloadTooLarge = new("operation_journal.payload_too_large", "来源操作日志载荷超出单条上限，未接纳新记录。");
    internal static readonly Error CapacityExceeded = new("operation_journal.capacity_exceeded", "来源操作日志容量已满，未接纳新记录。");
    internal static readonly Error IdentityConflict = new("operation_journal.identity_conflict", "操作日志身份已用于不同内容。");
}
