using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure;

/// <summary>开发用计划存储。读取隔离快照，编码唯一性和版本提交在同一把锁内裁决。</summary>
public sealed class InMemoryScheduledTaskStore(IIntegrationEventSerializer serializer) : IScheduledTaskStore, IOutboxStore
{
    private readonly Dictionary<long, ScheduledTask> _tasks = [];
    private readonly Lock _writes = new();
    private readonly Dictionary<Guid, ScheduleOccurrence> _occurrences = [];
    private readonly Dictionary<Guid, OutboxEntry> _outbox = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult<IReadOnlyList<ScheduledTask>>(_tasks.Values.Where(task => task.IsDue(now))
                .OrderBy(task => task.NextRunAt).ThenBy(task => task.Id.Value).Take(batchSize).Select(task => task.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult<IReadOnlyList<ScheduledTask>>(_tasks.Values.OrderBy(task => task.Code.Value, StringComparer.Ordinal)
                .Select(task => task.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes) { return Task.FromResult(_tasks.GetValueOrDefault(id.Value)?.Snapshot()); }
    }

    /// <inheritdoc />
    public Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, TaskRegistry.MaximumPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, TaskRegistry.MaximumPageSize);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult(new ScheduledTaskPage(_tasks.Values.OrderBy(task => task.Id.Value)
                .Skip((page - 1) * limit).Take(limit).Select(task => task.Snapshot()).ToArray(), _tasks.Count));
        }
    }

    /// <inheritdoc />
    public Task<Result> AddAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            if (_tasks.ContainsKey(task.Id.Value) || _tasks.Values.Any(existing => existing.Code.Equals(task.Code)))
            {
                return Task.FromResult(Result.Failure(TaskRegistry.CodeTaken));
            }
            _tasks.Add(task.Id.Value, task.Snapshot());
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            if (!_tasks.TryGetValue(task.Id.Value, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(Result.Failure(TaskRegistry.Conflict));
            }
            _tasks[task.Id.Value] = task.Snapshot();
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<Result> RecordOccurrenceAsync(ScheduledTask task, long expectedVersion, ScheduleOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(occurrence);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = OutboxEntry.From(occurrence.ToEvent(), serializer);
        lock (_writes)
        {
            if (_occurrences.TryGetValue(occurrence.OccurrenceId, out var existing))
            {
                return Task.FromResult(existing == occurrence ? Result.Success() : Result.Failure(TaskRegistry.Conflict));
            }
            if (!_tasks.TryGetValue(task.Id.Value, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(Result.Failure(TaskRegistry.Conflict));
            }
            _tasks[task.Id.Value] = task.Snapshot();
            _occurrences.Add(occurrence.OccurrenceId, occurrence);
            _outbox.Add(pending.Id, pending);
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var query = _occurrences.Values.Where(item => item.PlanId == planId).OrderByDescending(item => item.TriggerSequence);
            return Task.FromResult(new ScheduleOccurrencePage(query.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit)
                .Select(item => ScheduleOccurrenceDelivery.From(item, _outbox[item.OccurrenceId])).ToArray(), query.LongCount()));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }
    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.MarkDelivered(now), cancellationToken);
    /// <inheritdoc />
    public Task MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.RecordFailure(failure, nextAttemptAt), cancellationToken);
    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.MarkDeadLettered(failure, now), cancellationToken);

    private Task UpdateDeliveryAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes) { if (_outbox.TryGetValue(id, out var entry)) { _outbox[id] = update(entry); } }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            _outbox.TryGetValue(occurrenceId, out var entry);
            var retry = ScheduleOccurrenceDelivery.Retry(entry, expectedDeadLetteredAt);
            if (retry.IsFailure) { return Task.FromResult(Result.Failure<ScheduleOccurrenceDelivery>(retry.Error)); }
            _outbox[occurrenceId] = retry.Value;
            return Task.FromResult(Result.Success(ScheduleOccurrenceDelivery.From(_occurrences[occurrenceId], retry.Value)));
        }
    }
}

/// <summary>Scheduling 开发存储的显式装配。</summary>
public static class SchedulingInfrastructureServiceCollectionExtensions
{
    /// <summary>宿主装配时显式选择 Scheduling 的 Outbox。</summary>
    public const string OutboxKey = "scheduling";
    /// <summary>注册内存存储和应用入口。</summary>
    /// <param name="services">容器。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddSchedulingInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<InMemoryScheduledTaskStore>();
        services.AddSingleton<IScheduledTaskStore>(provider => provider.GetRequiredService<InMemoryScheduledTaskStore>());
        services.AddKeyedSingleton<IOutboxStore>(OutboxKey, (provider, _) => provider.GetRequiredService<InMemoryScheduledTaskStore>());
        services.AddScoped<ScheduleRunner>();
        services.AddScoped<TaskRegistry>();
        return services;
    }
}
