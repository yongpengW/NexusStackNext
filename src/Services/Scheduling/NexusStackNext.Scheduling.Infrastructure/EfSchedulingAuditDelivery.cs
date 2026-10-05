using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure.Persistence;

namespace NexusStackNext.Scheduling.Infrastructure;

internal sealed class EfSchedulingAuditDelivery(SchedulingDbContext context) : ISchedulingAuditDelivery
{
    public Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _recovery.GetAsync(messageId, cancellationToken);

    public Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
        => _recovery.ListAsync(context.Outbox, state, limit, cancellationToken);

    public Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
        => _recovery.CleanupRecoveriesAsync(batchSize, now, cancellationToken);

    private readonly PostgresFactDeliveryRecoveryStore _recovery = new(
        context.Database.GetConnectionString() ?? throw new InvalidOperationException("所属连接未配置。"),
        new(SchedulingDbContext.SchemaName, PlanCommittedV1.Name, SchedulingFactCapacityPolicyChangedV1.Name,
            new(SchedulingAuditRecoveryErrors.Invalid, SchedulingAuditRecoveryErrors.Conflict, SchedulingAuditRecoveryErrors.RequestConflict,
                SchedulingAuditRecoveryErrors.NotFound, SchedulingAuditRecoveryErrors.Exhausted, SchedulingAuditRecoveryErrors.Unmanaged, SchedulingAuditRecoveryErrors.DeliveryNotFound)));

    public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
        => _recovery.ReadRecoveryCapacityAsync(cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
        => _recovery.GetRecoveryAsync(requestId, cancellationToken);

    public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
        => _recovery.RecoverAsync(request, actorId, occurredAt, execution, cancellationToken);
}
