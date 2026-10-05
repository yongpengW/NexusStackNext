using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Files.Infrastructure;

internal sealed class InMemoryFileAuditDelivery(FilesMemoryState state,
    MemoryFactDeliveryRecoveryControlOptions? limits = null) : IFileAuditDelivery
{
    public Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _recovery.GetAsync(messageId, cancellationToken);

    public Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
        => _recovery.ListAsync(state, limit, cancellationToken);

    private readonly InMemoryFactDeliveryRecoveryStore _recovery = new(state.Capacity, () => state.Outbox,
        new("files", StoredFileCommittedV1.Name, FilesFactCapacityPolicyChangedV1.Name,
            new(FileAuditRecoveryErrors.Invalid, FileAuditRecoveryErrors.Conflict, FileAuditRecoveryErrors.RequestConflict,
                FileAuditRecoveryErrors.NotFound, FileAuditRecoveryErrors.Exhausted, FileAuditRecoveryErrors.Unmanaged, FileAuditRecoveryErrors.DeliveryNotFound)), limits);

    public Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
        => _recovery.CleanupRecoveriesAsync(batchSize, now, cancellationToken);

    public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
        => _recovery.ReadRecoveryCapacityAsync(cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
        => _recovery.RecoverAsync(request, actorId, occurredAt, execution, cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
        => _recovery.GetRecoveryAsync(requestId, cancellationToken);
}
