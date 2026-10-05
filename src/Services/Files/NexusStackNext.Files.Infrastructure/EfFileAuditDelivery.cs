using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure.Persistence;

namespace NexusStackNext.Files.Infrastructure;

internal sealed class EfFileAuditDelivery(FilesDbContext context) : IFileAuditDelivery
{
    public Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _recovery.GetAsync(messageId, cancellationToken);

    public Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
        => _recovery.ListAsync(context.Outbox, state, limit, cancellationToken);

    public Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
        => _recovery.CleanupRecoveriesAsync(batchSize, now, cancellationToken);

    private readonly PostgresFactDeliveryRecoveryStore _recovery = new(
        context.Database.GetConnectionString() ?? throw new InvalidOperationException("所属连接未配置。"),
        new(FilesDbContext.SchemaName, StoredFileCommittedV1.Name, FilesFactCapacityPolicyChangedV1.Name,
            new(FileAuditRecoveryErrors.Invalid, FileAuditRecoveryErrors.Conflict, FileAuditRecoveryErrors.RequestConflict,
                FileAuditRecoveryErrors.NotFound, FileAuditRecoveryErrors.Exhausted, FileAuditRecoveryErrors.Unmanaged, FileAuditRecoveryErrors.DeliveryNotFound)));

    public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
        => _recovery.ReadRecoveryCapacityAsync(cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
        => _recovery.GetRecoveryAsync(requestId, cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
        => _recovery.RecoverAsync(request, actorId, occurredAt, execution, cancellationToken);
}
