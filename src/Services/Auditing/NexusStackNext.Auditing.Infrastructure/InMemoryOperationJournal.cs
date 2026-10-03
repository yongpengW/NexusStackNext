using System.Text;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class InMemoryOperationJournal(OperationJournalCapacityOptions capacity, OperationJournalCleanupOptions cleanup, IClock clock)
    : IOperationJournal, IOperationJournalMaintenance, IOutboxStore
{
    public Task<Result<OperationJournalDelivery>> GetDeliveryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_records.TryGetValue(messageId, out var item)
                ? Result.Success(Delivery(item)) : Result.Failure<OperationJournalDelivery>(OperationJournalDeliveryErrors.NotFound));
        }
    }
    public Task<Result<OperationJournalDeadLetterPage>> QueryDeadLettersAsync(OperationJournalDeadLetterQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Validate().IsFailure) { return Task.FromResult(Result.Failure<OperationJournalDeadLetterPage>(OperationJournalDeliveryErrors.InvalidQuery)); }
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matching = _records.Values.Where(item => item.Entry.IsDeadLettered && !item.Entry.IsDelivered
                && (query.Source is null || item.Identity.Source == query.Source));
            var items = matching.OrderBy(item => item.Entry.DeadLetteredAt).ThenBy(item => item.Entry.Id)
                .Skip((query.Page - 1) * query.Limit).Take(query.Limit).Select(Delivery).ToArray();
            return Task.FromResult(Result.Success(new OperationJournalDeadLetterPage(query.Page, query.Limit, matching.LongCount(), items)));
        }
    }
    public Task<Result<OperationJournalRecoveryReceipt>> RetryDeliveryAsync(OperationJournalRecoveryRequest request,
        OperationJournalRecoveryActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Validate(actor).IsFailure) { return Task.FromResult(Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.Invalid)); }
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_recoveries.TryGetValue(request.RequestId, out var existing))
            {
                return Task.FromResult(existing.Request == request && existing.Actor == actor
                    ? Result.Success(existing) : Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.RequestConflict));
            }
            if (!_records.TryGetValue(request.MessageId, out var item))
            {
                return Task.FromResult(Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalDeliveryErrors.NotFound));
            }
            var retried = item.Entry.RetryDelivery(request.ExpectedDeadLetteredAt);
            if (retried is null || item.Entry.RetryRevision != request.ExpectedRetryRevision)
            {
                return Task.FromResult(Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalDeliveryErrors.Conflict));
            }
            if (_recoveries.Count >= capacity.MaxRecoveryRecords)
            {
                return Task.FromResult(Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.CapacityExceeded));
            }
            var recoveredAt = clock.UtcNow;
            var receipt = new OperationJournalRecoveryReceipt(request, actor, item.Identity.Source, recoveredAt, retried.RetryRevision,
                recoveredAt + cleanup.RecoveryRetention);
            cancellationToken.ThrowIfCancellationRequested();
            _recoveries.Add(request.RequestId, receipt);
            _records[request.MessageId] = item with { Entry = retried };
            return Task.FromResult(Result.Success(receipt));
        }
    }

    public Task<Result<OperationJournalRecoveryReceipt>> GetRecoveryAsync(Guid requestId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_recoveries.TryGetValue(requestId, out var receipt)
                ? Result.Success(receipt) : Result.Failure<OperationJournalRecoveryReceipt>(OperationJournalRecoveryErrors.NotFound));
        }
    }
    private readonly Dictionary<Guid, OperationJournalRecoveryReceipt> _recoveries = [];

    public Task<int> CleanupRecoveryRecordsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.UtcNow;
            var expired = _recoveries.Values.Where(item => item.RetainUntil <= now)
                .OrderBy(item => item.RetainUntil).ThenBy(item => item.Request.RequestId).Take(cleanup.BatchSize).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in expired) { _recoveries.Remove(item.Request.RequestId); }
            return Task.FromResult(expired.Length);
        }
    }
    private readonly Lock _gate = new();
    private sealed record RetainedObservation(OutboxEntry Entry, (string Source, Guid OperationId, string Phase) Identity);
    private static OperationJournalDelivery Delivery(RetainedObservation item) => new(item.Entry.Id,
        item.Identity.Source, item.Identity.OperationId, item.Identity.Phase,
        item.Entry.IsDelivered ? "Delivered" : item.Entry.IsDeadLettered ? "DeadLettered" : "Pending",
        item.Entry.AttemptCount, item.Entry.NextAttemptAt, item.Entry.DeadLetteredAt, item.Entry.DeliveredAt,
        item.Entry.RetryRevision, item.Entry.LastFailure is null ? null : "operation_journal.delivery_failed");
    private readonly Dictionary<Guid, RetainedObservation> _records = [];
    private readonly HashSet<(string Source, Guid OperationId, string Phase)> _phases = [];
    private long _payloadBytes;
    internal bool HasDeadLetters { get { lock (_gate) { return _records.Values.Any(item => item.Entry.IsDeadLettered); } } }
    internal (long Records, long PayloadBytes, long RecoveryRecords) Usage()
    {
        lock (_gate) { return (_records.Count, _payloadBytes, _recoveries.Count); }
    }

    public Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        var valid = OperationObservationIngestion.Validate(observation);
        if (valid.IsFailure) { return Task.FromResult(valid); }
        var record = OutboxEntry.From(observation, new SystemTextJsonIntegrationEventSerializer());
        lock (_gate)
        {
            if (_records.TryGetValue(record.Id, out var existing))
            {
                return Task.FromResult(existing.Entry.EventName == record.EventName && existing.Entry.Payload == record.Payload
                    ? Result.Success() : Result.Failure(OperationJournalErrors.IdentityConflict));
            }
            if (_phases.Contains((observation.Source, observation.OperationId, observation.Phase)))
            {
                return Task.FromResult(Result.Failure(OperationJournalErrors.IdentityConflict));
            }
            var bytes = Encoding.UTF8.GetByteCount(record.Payload);
            if (bytes > capacity.MaxRecordPayloadBytes) { return Task.FromResult(Result.Failure(OperationJournalErrors.PayloadTooLarge)); }
            if (_records.Count >= capacity.MaxRecords || bytes > capacity.MaxPayloadBytes - _payloadBytes)
            {
                return Task.FromResult(Result.Failure(OperationJournalErrors.CapacityExceeded));
            }
            cancellationToken.ThrowIfCancellationRequested();
            _phases.Add((observation.Source, observation.OperationId, observation.Phase));
            _records.Add(record.Id, new(record, (observation.Source, observation.OperationId, observation.Phase)));
            _payloadBytes += bytes;
            return Task.FromResult(Result.Success());
        }
    }

    public Task<int> CleanupDeliveredAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var cutoff = clock.UtcNow - cleanup.DeliveredRetention;
            var expired = _records.Values.Where(item => item.Entry.DeliveredAt <= cutoff && !item.Entry.IsDeadLettered)
                .OrderBy(item => item.Entry.DeliveredAt).ThenBy(item => item.Entry.Id).Take(cleanup.BatchSize).ToArray();
            var released = expired.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Entry.Payload));
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in expired)
            {
                _records.Remove(item.Entry.Id);
                _phases.Remove(item.Identity);
            }
            _payloadBytes -= released;
            return Task.FromResult(expired.Length);
        }
    }

    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_records.Values.Select(item => item.Entry)
                .Where(entry => entry.IsPending && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now))
                .OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }

    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.MarkDelivered(now), cancellationToken);
    public Task<bool> MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);
    public Task<bool> MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task<bool> UpdateAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            // 清理后的迟到投递结果不重建记录，与 PostgreSQL 适配器一致。
            if (!_records.TryGetValue(id, out var current)) { return Task.FromResult(false); }
            var updated = update(current.Entry);
            _records[id] = current with { Entry = updated };
            return Task.FromResult(updated != current.Entry);
        }
    }
}
