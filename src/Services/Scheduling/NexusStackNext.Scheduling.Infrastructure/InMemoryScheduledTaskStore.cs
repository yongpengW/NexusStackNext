using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure;

/// <summary>开发用计划存储。读取隔离快照，编码唯一性和版本提交在同一把锁内裁决。</summary>
public sealed class InMemoryScheduledTaskStore : IScheduledTaskStore, IOutboxStore
{
    private readonly SchedulingMemoryState _state;
    private readonly IIntegrationEventSerializer _serializer;
    private readonly ScheduledPlanCommittedFacts _facts;

    /// <summary>创建独立的内存存储；宿主装配使用共享状态及每次调用的事实上下文。</summary>
    /// <param name="serializer">消息序列化。</param>
    /// <param name="clock">独立应用的事实时钟。</param>
    /// <param name="capacity">本存储的事实保留上限。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    public InMemoryScheduledTaskStore(IIntegrationEventSerializer serializer, IClock? clock = null, MemoryCommittedFactCapacityOptions? capacity = null,
        CommittedFactCapacityWriteOptions? write = null)
        : this(serializer, new SchedulingMemoryState(capacity, write), new ScheduledPlanCommittedFacts(clock ?? new SystemClock(), serializer)) { }

    internal InMemoryScheduledTaskStore(IIntegrationEventSerializer serializer, SchedulingMemoryState state, ScheduledPlanCommittedFacts facts)
    {
        _serializer = serializer;
        _state = state;
        _facts = facts;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            return Task.FromResult<IReadOnlyList<ScheduledTask>>(_state.Tasks.Values.Where(task => task.IsDue(now))
                .OrderBy(task => task.NextRunAt).ThenBy(task => task.Id.Value).Take(batchSize).Select(task => task.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            return Task.FromResult<IReadOnlyList<ScheduledTask>>(_state.Tasks.Values.OrderBy(task => task.Code.Value, StringComparer.Ordinal)
                .Select(task => task.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken)) { return Task.FromResult(_state.Tasks.GetValueOrDefault(id.Value)?.Snapshot()); }
    }

    /// <inheritdoc />
    public Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, TaskRegistry.MaximumPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, TaskRegistry.MaximumPageSize);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            return Task.FromResult(new ScheduledTaskPage(_state.Tasks.Values.OrderBy(task => task.Id.Value)
                .Skip((page - 1) * limit).Take(limit).Select(task => task.Snapshot()).ToArray(), _state.Tasks.Count));
        }
    }

    /// <inheritdoc />
    public Task<Result> AddAsync(ScheduledTask task, ExecutionOrigin? origin = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_state.Capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state.Tasks.ContainsKey(task.Id.Value) || _state.Tasks.Values.Any(existing => existing.Code.Equals(task.Code)))
            {
                return Task.FromResult(Result.Failure(TaskRegistry.CodeTaken));
            }
            var facts = _facts.Create(null, task);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = task.Snapshot();
            if (!_state.Capacity.TryCommit(facts, () =>
            {
                _state.Tasks.Add(task.Id.Value, snapshot);
                _state.Origins.Add(task.Id.Value, origin);
                foreach (var fact in facts) { _state.Outbox.Add(fact.Id, fact); }
            }, cancellationToken)) { return Task.FromResult(Result.Failure(TaskRegistry.AuditCapacityExceeded)); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<ExecutionOrigin?> ReadExecutionOriginAsync(ScheduledTaskId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken)) { return Task.FromResult(_state.Origins.GetValueOrDefault(id.Value)); }
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_state.Capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_state.Tasks.TryGetValue(task.Id.Value, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(Result.Failure(TaskRegistry.Conflict));
            }
            var facts = _facts.Create(current, task);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = task.Snapshot();
            if (!_state.Capacity.TryCommit(facts, () =>
            {
                _state.Tasks[task.Id.Value] = snapshot;
                foreach (var fact in facts) { _state.Outbox.Add(fact.Id, fact); }
            }, cancellationToken)) { return Task.FromResult(Result.Failure(TaskRegistry.AuditCapacityExceeded)); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<Result> RecordDecisionAsync(ScheduledTask task, long expectedVersion, ScheduleDecision decision, ScheduleOccurrence? occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.OccurrenceId != occurrence?.OccurrenceId) { return Task.FromResult(Result.Failure(TaskRegistry.Conflict)); }
        var pending = occurrence is null ? null : OutboxEntry.From(occurrence.ToEvent(), _serializer);
        if (!_state.Capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state.Decisions.TryGetValue(decision.DecisionId, out var existing))
            {
                var original = decision.OccurrenceId is { } id ? _state.Occurrences.GetValueOrDefault(id) : null;
                return Task.FromResult(existing == decision && original == occurrence ? Result.Success() : Result.Failure(TaskRegistry.Conflict));
            }
            if (!_state.Tasks.TryGetValue(task.Id.Value, out var current) || current.Version != expectedVersion
                || occurrence is not null && _state.Occurrences.ContainsKey(occurrence.OccurrenceId))
            {
                return Task.FromResult(Result.Failure(TaskRegistry.Conflict));
            }
            var facts = _facts.Create(current, task, decision);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = task.Snapshot();
            if (!_state.Capacity.TryCommit(facts, () =>
            {
                _state.Tasks[task.Id.Value] = snapshot;
                _state.Decisions.Add(decision.DecisionId, decision);
                foreach (var fact in facts) { _state.Outbox.Add(fact.Id, fact); }
                if (occurrence is not null)
                {
                    _state.Occurrences.Add(occurrence.OccurrenceId, occurrence);
                    _state.Outbox.Add(pending!.Id, pending);
                }
            }, cancellationToken)) { return Task.FromResult(Result.Failure(TaskRegistry.AuditCapacityExceeded)); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<ScheduleDecisionPage> ReadDecisionsAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            var query = _state.Decisions.Values.Where(item => item.PlanId == planId).OrderByDescending(item => item.PlanVersion);
            return Task.FromResult(new ScheduleDecisionPage(query.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit).ToArray(), query.LongCount()));
        }
    }

    /// <inheritdoc />
    public Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            var query = _state.Occurrences.Values.Where(item => item.PlanId == planId).OrderByDescending(item => item.TriggerSequence);
            return Task.FromResult(new ScheduleOccurrencePage(query.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit)
                .Select(item => ScheduleOccurrenceDelivery.From(item, _state.Outbox[item.OccurrenceId])).ToArray(), query.LongCount()));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_state.Outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }
    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.MarkDelivered(now), cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task<bool> UpdateDeliveryAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_state.Capacity.Enter(cancellationToken))
        {
            if (!_state.Outbox.TryGetValue(id, out var entry)) { return Task.FromResult(false); }
            var updated = update(entry);
            _state.Outbox[id] = updated;
            return Task.FromResult(updated != entry);
        }
    }

    /// <inheritdoc />
    public Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_state.Capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure<ScheduleOccurrenceDelivery>(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            _state.Outbox.TryGetValue(occurrenceId, out var entry);
            var retry = ScheduleOccurrenceDelivery.Retry(entry, expectedDeadLetteredAt);
            if (retry.IsFailure) { return Task.FromResult(Result.Failure<ScheduleOccurrenceDelivery>(retry.Error)); }
            _state.Outbox[occurrenceId] = retry.Value;
            return Task.FromResult(Result.Success(ScheduleOccurrenceDelivery.From(_state.Occurrences[occurrenceId], retry.Value)));
        }
    }
}

internal sealed class SchedulingMemoryState : ICommittedFactCapacityReader
{
    public Task<Result<CommittedFactCapacitySnapshot>> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Capacity.Read("scheduling", cancellationToken));

    internal SchedulingMemoryState(MemoryCommittedFactCapacityOptions? capacity = null, CommittedFactCapacityWriteOptions? write = null)
    {
        Capacity = new(Writes, PlanCommittedV1.Name, capacity, write);
    }

    internal InMemoryCommittedFactCapacity Capacity { get; }
    internal Dictionary<long, ScheduledTask> Tasks { get; } = [];
    internal Lock Writes { get; } = new();
    internal Dictionary<Guid, ScheduleOccurrence> Occurrences { get; } = [];
    internal Dictionary<Guid, ScheduleDecision> Decisions { get; } = [];
    internal Dictionary<Guid, OutboxEntry> Outbox { get; set; } = [];
    internal Dictionary<long, ExecutionOrigin?> Origins { get; } = [];
}

/// <summary>Scheduling 开发存储的显式装配。</summary>
public static class SchedulingInfrastructureServiceCollectionExtensions
{
    /// <summary>宿主装配时显式选择 Scheduling 的 Outbox。</summary>
    public const string OutboxKey = "scheduling";
    /// <summary>注册内存存储和应用入口。</summary>
    /// <param name="services">容器。</param>
    /// <param name="capacity">本存储的事实保留上限。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    /// <param name="control">所属 Memory 控制池的启动上限。</param>
    /// <param name="recovery">所属 Memory 恢复凭据池的启动上限。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddSchedulingInMemoryStorage(this IServiceCollection services, MemoryCommittedFactCapacityOptions? capacity = null,
        CommittedFactCapacityWriteOptions? write = null, MemoryFactCapacityPolicyControlOptions? control = null, MemoryFactDeliveryRecoveryControlOptions? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var controlLimits = control ?? new();
        controlLimits.Validate();
        var recoveryLimits = recovery ?? new();
        recoveryLimits.Validate();
        services.AddSingleton(new SchedulingMemoryState(capacity, write));
        services.AddSingleton<ISchedulingAuditDelivery>(provider => new InMemorySchedulingAuditDelivery(
            provider.GetRequiredService<SchedulingMemoryState>(), recoveryLimits));
        services.AddKeyedSingleton<InMemoryFactCapacityPolicyStore>(OutboxKey, (provider, _) =>
        {
            var state = provider.GetRequiredService<SchedulingMemoryState>();
            return new(state.Writes, state.Capacity, () => state.Outbox, prepared => state.Outbox = prepared,
                new("scheduling", SchedulingFactCapacityPolicyChangedV1.From), provider.GetRequiredService<IIntegrationEventSerializer>(), controlLimits);
        });
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyStore>(OutboxKey, (provider, _) => provider.GetRequiredKeyedService<InMemoryFactCapacityPolicyStore>(OutboxKey));
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyCleanup>(OutboxKey, (provider, _) => provider.GetRequiredKeyedService<InMemoryFactCapacityPolicyStore>(OutboxKey));
        services.AddKeyedSingleton<ICommittedFactCapacityReader>(OutboxKey, (provider, _) => provider.GetRequiredService<SchedulingMemoryState>());
        services.AddScoped<ScheduledPlanCommittedFacts>();
        services.AddScoped(provider => new InMemoryScheduledTaskStore(provider.GetRequiredService<IIntegrationEventSerializer>(),
            provider.GetRequiredService<SchedulingMemoryState>(), provider.GetRequiredService<ScheduledPlanCommittedFacts>()));
        services.AddScoped<IScheduledTaskStore>(provider => provider.GetRequiredService<InMemoryScheduledTaskStore>());
        services.AddKeyedScoped<IOutboxStore>(OutboxKey, (provider, _) => provider.GetRequiredService<InMemoryScheduledTaskStore>());
        services.AddScoped<ScheduleRunner>();
        services.AddScoped<TaskRegistry>();
        return services;
    }
}
