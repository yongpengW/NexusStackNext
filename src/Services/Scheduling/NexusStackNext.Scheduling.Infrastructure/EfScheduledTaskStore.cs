using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using Npgsql;

namespace NexusStackNext.Scheduling.Infrastructure;

internal sealed class EfScheduledTaskStore(SchedulingDbContext context, IIntegrationEventSerializer serializer) : IScheduledTaskStore
{
    public async Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default) =>
        await context.Plans.AsNoTracking().Where(task => task.IsEnabled && task.NextRunAt <= now && (task.RetryAt == null || task.RetryAt <= now))
            .OrderBy(task => task.NextRunAt).ThenBy(task => task.Id).Take(batchSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Plans.AsNoTracking().OrderBy(task => task.Code).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) =>
        context.Plans.AsNoTracking().SingleOrDefaultAsync(task => task.Id == id, cancellationToken);

    public async Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, TaskRegistry.MaximumPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, TaskRegistry.MaximumPageSize);
        var total = await context.Plans.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await context.Plans.AsNoTracking().OrderBy(task => task.Id).Skip((page - 1) * limit).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new(items, total);
    }

    public Task<Result> AddAsync(ScheduledTask task, ExecutionOrigin? origin = null, CancellationToken cancellationToken = default)
    {
        context.Plans.Add(task);
        context.Entry(task).Property<ExecutionOrigin?>(SchedulingDbContext.ExecutionOriginProperty).CurrentValue = origin;
        return CommitAsync(cancellationToken);
    }

    public Task<ExecutionOrigin?> ReadExecutionOriginAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) =>
        context.Plans.AsNoTracking().Where(task => task.Id == id)
            .Select(task => EF.Property<ExecutionOrigin?>(task, SchedulingDbContext.ExecutionOriginProperty)).SingleOrDefaultAsync(cancellationToken);

    public Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.Clear();
        var entry = context.Update(task);
        entry.Property(value => value.Version).OriginalValue = expectedVersion;
        return CommitAsync(cancellationToken);
    }

    private async Task<Result> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException) { return Result.Failure(TaskRegistry.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_plans_code",
        })
        { return Result.Failure(TaskRegistry.CodeTaken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_occurrences_plan_sequence" or "ux_decisions_plan_version",
        })
        { return Result.Failure(TaskRegistry.Conflict); }
        finally { context.ChangeTracker.Clear(); }
    }

    public Task<Result> RecordDecisionAsync(ScheduledTask task, long expectedVersion, ScheduleDecision decision, ScheduleOccurrence? occurrence, CancellationToken cancellationToken = default) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            if (decision.OccurrenceId != occurrence?.OccurrenceId) { return Result.Failure(TaskRegistry.Conflict); }
            var existing = await context.Decisions.AsNoTracking().SingleOrDefaultAsync(item => item.DecisionId == decision.DecisionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var original = decision.OccurrenceId is { } id
                    ? await context.Occurrences.AsNoTracking().SingleOrDefaultAsync(item => item.OccurrenceId == id, cancellationToken).ConfigureAwait(false) : null;
                return existing == decision && original == occurrence ? Result.Success() : Result.Failure(TaskRegistry.Conflict);
            }
            // 计划、决定、可选发生与 Outbox 使用同一次 SaveChanges 的事务；重试不再次推进聚合。
            var entry = context.Update(task.Snapshot());
            entry.Property(value => value.Version).OriginalValue = expectedVersion;
            context.Decisions.Add(decision);
            if (occurrence is not null)
            {
                context.Occurrences.Add(occurrence);
                context.Outbox.Add(OutboxEntry.From(occurrence.ToEvent(), serializer));
            }
            return await CommitAsync(cancellationToken).ConfigureAwait(false);
        });

    public async Task<ScheduleDecisionPage> ReadDecisionsAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var query = context.Decisions.AsNoTracking().Where(item => item.PlanId == planId);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(item => item.PlanVersion).Skip((int)Math.Min(offset, int.MaxValue)).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new(items, total);
    }

    public async Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var query = context.Occurrences.AsNoTracking().Where(item => item.PlanId == planId);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(item => item.TriggerSequence).Skip((int)Math.Min(offset, int.MaxValue)).Take(limit)
            .Join(context.Outbox.AsNoTracking(), item => item.OccurrenceId, entry => entry.Id,
                (item, entry) => new { Occurrence = item, Entry = entry }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new(items.Select(item => ScheduleOccurrenceDelivery.From(item.Occurrence, item.Entry)).ToArray(), total);
    }

    public Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var entry = (await context.Outbox.FromSqlInterpolated($"SELECT * FROM scheduling.outbox WHERE \"Id\" = {occurrenceId} FOR UPDATE")
                .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            var retry = ScheduleOccurrenceDelivery.Retry(entry, expectedDeadLetteredAt);
            if (retry.IsFailure) { return Result.Failure<ScheduleOccurrenceDelivery>(retry.Error); }
            var occurrence = await context.Occurrences.AsNoTracking().SingleAsync(item => item.OccurrenceId == occurrenceId, cancellationToken).ConfigureAwait(false);
            context.Entry(entry!).CurrentValues.SetValues(retry.Value);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(ScheduleOccurrenceDelivery.From(occurrence, retry.Value));
        });
}

/// <summary>Scheduling PostgreSQL 存储与应用入口的装配。</summary>
public static class SchedulingPersistenceServiceCollectionExtensions
{
    /// <summary>注册本上下文存储；启动时验证已完成独立迁移。</summary>
    /// <param name="services">容器。</param>
    /// <param name="connectionString">Scheduling 连接配置。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddSchedulingPostgresStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<SchedulingDbContext>((provider, options) => options
            .UseNexusStackPostgres(connectionString, SchedulingDbContext.SchemaName).UseNexusStackInterceptors(provider));
        services.AddScoped<IScheduledTaskStore, EfScheduledTaskStore>();
        services.AddKeyedScoped<IOutboxStore, EfOutboxStore<SchedulingDbContext>>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey);
        services.AddScoped<TaskRegistry>();
        services.AddScoped<ScheduleRunner>();
        services.AddHostedService<SchedulingDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<SchedulingDatabaseHealthCheck>("scheduling-database");
        return services;
    }
}
