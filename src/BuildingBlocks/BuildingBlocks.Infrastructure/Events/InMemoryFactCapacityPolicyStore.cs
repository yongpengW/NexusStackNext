using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

// Policy publication uses the original business gate, but never commits a business working copy.
/// <summary>在所属共用锁内准备、裁决并发布有限策略记录，不提交业务工作副本。</summary>
/// <param name="gate">与所属业务写入共用的锁。</param>
/// <param name="capacity">原有业务事实容量账本。</param>
/// <param name="readOutbox">只在所属锁内读取当前已提交消息字典。</param>
/// <param name="publishOutbox">发布预备好的消息字典，不分配或执行外部工作。</param>
/// <param name="source">模块声明的固定来源与契约投影。</param>
/// <param name="serializer">事实序列化器。</param>
/// <param name="control">仅在创建所属内存存储时决定的有限控制额度。</param>
public sealed class InMemoryFactCapacityPolicyStore(Lock gate, InMemoryCommittedFactCapacity capacity,
    Func<Dictionary<Guid, OutboxEntry>> readOutbox, Action<Dictionary<Guid, OutboxEntry>> publishOutbox,
    FactCapacityPolicySource source, IIntegrationEventSerializer serializer, MemoryFactCapacityPolicyControlOptions? control = null)
    : ICommittedFactCapacityPolicyStore, ICommittedFactCapacityPolicyCleanup
{
    private Dictionary<Guid, PolicyRecord> _receipts = [];
    private long _revision = 1;
    private long _bytes;
    private readonly MemoryFactCapacityPolicyControlOptions _control = ValidatedControl(control);

    private sealed record PolicyRecord(FactCapacityPolicyRequest Request, string ActorId,
        FactCapacityPolicyReceipt Receipt, int PayloadBytes);

    private static MemoryFactCapacityPolicyControlOptions ValidatedControl(MemoryFactCapacityPolicyControlOptions? options)
    {
        var limits = options ?? new();
        limits.Validate();
        return limits;
    }

    /// <inheritdoc />
    public Task<int> CleanupAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("策略凭据清理必须使用独立的维护作用域。");
        }
        if (!capacity.TryEnter(out var scope, cancellationToken)) { throw new CommittedFactCapacityBusyException(); }
        using (scope)
        {
            var candidates = _receipts.Values.Where(record => record.Receipt.RetainUntil <= now
                && (record.Receipt.EventId is null || (readOutbox().TryGetValue(record.Receipt.EventId.Value, out var fact)
                    && fact.EventName == source.EventName && fact.DeliveredAt is not null
                    && fact.DeadLetteredAt is null && now >= DateTimeOffset.MinValue.AddDays(1)
                    && fact.DeliveredAt <= now.AddDays(-1))))
                .OrderBy(record => record.Receipt.RetainUntil).ThenBy(record => record.Request.RequestId)
                .Take(batchSize).ToArray();
            if (candidates.Length == 0) { return Task.FromResult(0); }
            var releasedBytes = candidates.Sum(record => (long)record.PayloadBytes);
            if (releasedBytes > _bytes) { throw new InvalidOperationException("策略凭据容量计量不一致。"); }
            var nextOutbox = new Dictionary<Guid, OutboxEntry>(readOutbox());
            var nextReceipts = new Dictionary<Guid, PolicyRecord>(_receipts);
            foreach (var record in candidates)
            {
                nextReceipts.Remove(record.Request.RequestId);
                if (record.Receipt.EventId is Guid eventId) { nextOutbox.Remove(eventId); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            publishOutbox(nextOutbox);
            _receipts = nextReceipts;
            _bytes -= releasedBytes;
            return Task.FromResult(candidates.Length);
        }
    }

    /// <inheritdoc />
    public Task<Result<FactCapacityPolicySnapshot>> ReadPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!gate.TryEnter())
        {
            return Task.FromResult(Result.Failure<FactCapacityPolicySnapshot>(CommittedFactCapacityErrors.Unavailable));
        }
        try
        {
            var business = capacity.Read(source.Context, cancellationToken);
            return Task.FromResult(Result.Success(new FactCapacityPolicySnapshot(business.Value, _revision,
                new(_control.MaxRecords, _control.MaxPayloadBytes, _control.MaxRecordPayloadBytes, _receipts.Count, _bytes))));
        }
        finally { gate.Exit(); }
    }

    /// <inheritdoc />
    public Task<Result<FactCapacityPolicyReceipt>> AdjustAsync(FactCapacityPolicyRequest request,
        string actorId, DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FactCapacityPolicyPreparation.IsValid(request, actorId, occurredAt, execution))
        {
            return Task.FromResult(Result.Failure<FactCapacityPolicyReceipt>(source.Invalid));
        }
        if (!capacity.TryEnter(out var scope, cancellationToken))
        {
            return Task.FromResult(Result.Failure<FactCapacityPolicyReceipt>(CommittedFactCapacityErrors.Busy));
        }
        using (scope)
        {
            if (_receipts.TryGetValue(request.RequestId, out var original))
            {
                return Task.FromResult(original.Request == request && original.ActorId == actorId
                    ? Result.Success(original.Receipt)
                    : Result.Failure<FactCapacityPolicyReceipt>(source.Conflict));
            }
            if (request.ExpectedPolicyRevision != _revision)
            {
                return Task.FromResult(Result.Failure<FactCapacityPolicyReceipt>(source.Conflict));
            }
            var business = capacity.Read(source.Context, cancellationToken).Value;
            var previous = new FactCapacityPolicyLimits(business.MaxRecords, business.MaxPayloadBytes, business.MaxRecordPayloadBytes);
            var next = request.Limits;
            var changed = previous != next;
            if (changed && _revision == long.MaxValue)
            {
                return Task.FromResult(Result.Failure<FactCapacityPolicyReceipt>(source.Conflict));
            }
            var prepared = FactCapacityPolicyPreparation.Prepare(request, actorId, occurredAt, execution,
                previous, _revision, source, serializer);
            var receipt = prepared.Receipt;
            var control = prepared.Fact;
            var revision = receipt.PolicyRevision;
            var size = prepared.PayloadBytes;
            if (_receipts.Count >= _control.MaxRecords || size > _control.MaxRecordPayloadBytes || size > _control.MaxPayloadBytes - _bytes)
            {
                return Task.FromResult(Result.Failure<FactCapacityPolicyReceipt>(source.Exhausted));
            }
            var nextOutbox = new Dictionary<Guid, OutboxEntry>(readOutbox());
            if (control is not null) { nextOutbox.Add(control.Id, control); }
            var nextReceipts = new Dictionary<Guid, PolicyRecord>(_receipts)
            {
                [request.RequestId] = new(request, actorId, receipt, size),
            };
            capacity.ChangePolicy(new MemoryCommittedFactCapacityOptions
            {
                MaxRecords = next.MaxRecords,
                MaxPayloadBytes = next.MaxPayloadBytes,
                MaxRecordPayloadBytes = next.MaxRecordPayloadBytes,
            }, () =>
            {
                publishOutbox(nextOutbox);
                _receipts = nextReceipts;
                _revision = revision;
                _bytes += size;
            }, cancellationToken);
            return Task.FromResult(Result.Success(receipt));
        }
    }
}
