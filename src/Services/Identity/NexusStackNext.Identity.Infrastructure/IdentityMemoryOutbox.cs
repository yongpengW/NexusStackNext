using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Identity.Infrastructure;

internal sealed class IdentityMemoryOutbox(IdentityMemoryState state) : IOutboxStore
{
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (state.Capacity.Enter(cancellationToken))
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(state.Outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id).Take(batchSize).ToArray());
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
        token.ThrowIfCancellationRequested();
        using (state.Capacity.Enter(token))
        {
            if (!state.Outbox.TryGetValue(id, out var before)) { return Task.FromResult(false); }
            var after = update(before);
            state.Outbox[id] = after;
            return Task.FromResult(after != before);
        }
    }
}
