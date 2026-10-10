using NexusStackNext.Auditing.Application;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class InMemoryAuditCapacityReader(InMemoryAuditEntryStore facts, InMemoryOperationObservationStore observations,
    AuditStorageCapacityOptions capacity) : IAuditStorageCapacityReader
{
    public Task<AuditStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(capacity.WaitTimeoutMilliseconds);
        try { return Task.FromResult(new AuditStorageCapacitySnapshot(facts.ReadCapacity(budget.Token), observations.ReadCapacity(budget.Token))); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        { throw new AuditStorageUnavailableException(false); }
    }
}
