using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>显式开发演示存储；同一把锁维护消息与阶段身份。</summary>
public sealed class InMemoryOperationObservationStore : IOperationObservationStore
{
    private readonly Dictionary<OperationObservationId, OperationObservation> _messages = new();
    private readonly Dictionary<(string Source, OperationId OperationId, string Phase), OperationObservationId> _phases = new();
    private readonly Lock _writes = new();

    /// <inheritdoc />
    public Task<Result<IngestionOutcome>> AcceptAsync(OperationObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            if (_messages.TryGetValue(observation.Id, out var existing))
            {
                return Task.FromResult(existing.Data == observation.Data ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(OperationObservationIngestion.MessageConflict));
            }
            var key = (observation.Data.Source, observation.Data.OperationId, observation.Data.Phase);
            if (!_phases.TryAdd(key, observation.Id))
            {
                return Task.FromResult(Result.Failure<IngestionOutcome>(OperationObservationIngestion.PhaseConflict));
            }
            _messages.Add(observation.Id, observation);
            return Task.FromResult(Result.Success(IngestionOutcome.Accepted));
        }
    }

    /// <inheritdoc />
    public Task<OperationPage> QueryAsync(OperationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Validate().IsFailure) { throw new ArgumentException("操作查询条件无效。", nameof(query)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var found = _messages.Values.GroupBy(item => (item.Data.Source, item.Data.OperationId)).Select(OperationSummary.From)
                .Where(item => (query.Source is null || item.Source == query.Source)
                    && (query.OperationId is null || item.OperationId == query.OperationId)
                    && (query.Outcome is null || item.Outcome == query.Outcome)
                    && (query.ActorId is null || item.ActorId == query.ActorId)
                    && (query.TraceId is null || item.TraceId == query.TraceId)
                    && (query.From is null || (item.FinishedAt ?? item.StartedAt) >= query.From)
                    && (query.To is null || (item.FinishedAt ?? item.StartedAt) <= query.To)).ToArray();
            var page = found.OrderByDescending(item => item.FinishedAt ?? item.StartedAt)
                .ThenBy(item => item.Source, StringComparer.Ordinal).ThenBy(item => item.OperationId)
                .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToArray();
            return Task.FromResult(new OperationPage(page, found.LongLength));
        }
    }
}
