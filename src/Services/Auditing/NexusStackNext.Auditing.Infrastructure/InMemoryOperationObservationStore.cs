using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>显式开发演示存储；同一把锁维护消息与阶段身份。</summary>
/// <param name="clock">默认调查窗口的时钟。</param>
/// <param name="capacity">本实例有限接纳配置。</param>
public sealed class InMemoryOperationObservationStore(IClock clock, AuditStorageCapacityOptions? capacity = null) : IOperationObservationStore
{
    private readonly AuditStorageCapacityOptions _capacity = (capacity ?? new()).Validate();
    private readonly Dictionary<OperationObservationId, OperationObservation> _messages = new();
    private readonly Dictionary<(string Source, OperationId OperationId, string Phase), OperationObservationId> _phases = new();
    private readonly Lock _writes = new();

    /// <inheritdoc />
    public Task<Result<IngestionOutcome>> AcceptAsync(OperationObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        using (AuditMemoryWriteLock.Enter(_writes, _capacity.WaitTimeoutMilliseconds, cancellationToken))
        {
            if (_messages.TryGetValue(observation.Id, out var existing))
            {
                return Task.FromResult(existing.Data == observation.Data ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(OperationObservationIngestion.MessageConflict));
            }
            var key = (observation.Data.Source, observation.Data.OperationId, observation.Data.Phase);
            if (_phases.ContainsKey(key))
            {
                return Task.FromResult(Result.Failure<IngestionOutcome>(OperationObservationIngestion.PhaseConflict));
            }
            if (_messages.Count >= _capacity.MaxObservations) { throw new AuditStorageUnavailableException(true); }
            _phases.Add(key, observation.Id);
            _messages.Add(observation.Id, observation);
            return Task.FromResult(Result.Success(IngestionOutcome.Accepted));
        }
    }

    internal AuditStoragePoolCapacity ReadCapacity(CancellationToken cancellationToken)
    {
        using var scope = AuditMemoryWriteLock.Enter(_writes, _capacity.WaitTimeoutMilliseconds, cancellationToken);
        return new(_messages.Count, _capacity.MaxObservations);
    }

    /// <inheritdoc />
    public Task<int> DeleteExpiredAsync(DateTimeOffset recordedBefore, int maxOperations, CancellationToken cancellationToken = default)
    {
        if (recordedBefore == default || recordedBefore.Offset != TimeSpan.Zero) { throw new ArgumentException("截止时刻必须为 UTC。", nameof(recordedBefore)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOperations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOperations, 1000);
        using var scope = AuditMemoryWriteLock.Enter(_writes, _capacity.WaitTimeoutMilliseconds, cancellationToken);
        var expired = _messages.Values.GroupBy(item => (item.Data.Source, item.Data.OperationId))
            .Where(group => group.All(item => item.RecordedAt < recordedBefore))
            .OrderBy(group => group.Max(item => item.RecordedAt)).ThenBy(group => group.Key.Source, StringComparer.Ordinal)
            .ThenBy(group => group.Key.OperationId.Value).Take(maxOperations).SelectMany(group => group).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var observation in expired)
        {
            _messages.Remove(observation.Id);
            _phases.Remove((observation.Data.Source, observation.Data.OperationId, observation.Data.Phase));
        }
        return Task.FromResult(expired.Length);
    }

    /// <inheritdoc />
    public Task<OperationPage> QueryAsync(OperationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalize(clock.UtcNow);
        if (query.Validate().IsFailure) { throw new ArgumentException("操作查询条件无效。", nameof(query)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var matches = query.Predicate().Compile();
            var found = _messages.Values.GroupBy(item => (item.Data.Source, item.Data.OperationId))
                .Where(group => matches(group.SingleOrDefault(item => item.Data.Phase == "finished") ?? group.Single()))
                .Select(OperationSummary.From).ToArray();
            var page = found.OrderByDescending(item => item.FinishedAt ?? item.StartedAt)
                .ThenBy(item => item.Source, StringComparer.Ordinal).ThenBy(item => item.OperationId)
                .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToArray();
            return Task.FromResult(new OperationPage(page, found.LongLength));
        }
    }
}
