using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>在来源原业务写锁内管理恢复、凭据及独立有限计量，不修改业务工作副本。</summary>
public sealed class InMemoryFactDeliveryRecoveryStore
{
    private readonly InMemoryCommittedFactCapacity _capacity;
    private readonly Func<Dictionary<Guid, OutboxEntry>> _readOutbox;
    private readonly FactDeliveryRecoverySource _source;
    private readonly MemoryFactDeliveryRecoveryControlOptions _limits;
    private Dictionary<Guid, StoredRecovery> _receipts = [];
    private long _bytes;

    /// <summary>接入来源当前的已提交 Outbox，所有读取及发布均在原共用写锁内。</summary>
    /// <param name="capacity">来源原写锁及其等待预算。</param>
    /// <param name="readOutbox">取得所属当前已提交字典；不得返回业务工作副本。</param>
    /// <param name="source">模块声明的归属、事件与拒绝语义。</param>
    /// <param name="limits">独立恢复池启动上限。</param>
    public InMemoryFactDeliveryRecoveryStore(InMemoryCommittedFactCapacity capacity,
        Func<Dictionary<Guid, OutboxEntry>> readOutbox, FactDeliveryRecoverySource source,
        MemoryFactDeliveryRecoveryControlOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(readOutbox);
        ArgumentNullException.ThrowIfNull(source);
        _capacity = capacity;
        _readOutbox = readOutbox;
        _source = source;
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <summary>按稳定消息身份读取所属可管理事实的安全状态。</summary>
    /// <param name="messageId">所属消息标识。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>安全状态、未找到或明确繁忙。</returns>
    public Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_capacity.TryEnter(out var scope, cancellationToken))
        { return Task.FromResult(Result.Failure<FactDeliveryState>(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _readOutbox().GetValueOrDefault(messageId);
            return Task.FromResult(entry is not null && _source.Manages(entry.EventName)
                ? Result.Success(FactDeliveryState.From(entry)) : Result.Failure<FactDeliveryState>(_source.Errors.DeliveryNotFound));
        }
    }

    /// <summary>在原共用写锁内有界读取所属状态，顺序稳定且不修改消息。</summary>
    /// <param name="state">Pending / Delivered / DeadLettered。</param>
    /// <param name="limit">一至一百。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>安全投递状态列表。</returns>
    public Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        if (state is not ("Pending" or "Delivered" or "DeadLettered")) { throw new ArgumentException("未知投递状态。", nameof(state)); }
        cancellationToken.ThrowIfCancellationRequested();
        using (_capacity.Enter(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FactDeliveryState>>(_readOutbox().Values
                .Where(entry => _source.Manages(entry.EventName)).OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id)
                .Select(FactDeliveryState.From).Where(entry => entry.State == state).Take(limit).ToArray());
        }
    }

    /// <summary>按原共用锁读取独立恢复池的最小计量。</summary>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>明确非持久的安全快照或所属拒绝。</returns>
    public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_capacity.TryEnter(out var scope, cancellationToken))
        { return Task.FromResult(Result.Failure<FactDeliveryRecoveryCapacity>(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success(new FactDeliveryRecoveryCapacity(_source.Context, false,
                new(_limits.MaxRecords, _limits.MaxPayloadBytes, _limits.MaxRecordPayloadBytes, _receipts.Count, _bytes))));
        }
    }

    /// <summary>恢复或重放原裁决，准备完成后才发布凭据、原消息预算和计量。</summary>
    /// <param name="request">稳定双条件请求。</param>
    /// <param name="actorId">可信操作者。</param>
    /// <param name="occurredAt">可信接受时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <param name="cancellationToken">实际发布前可取消。</param>
    /// <returns>稳定原裁决或所属拒绝。</returns>
    public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsValidFor(actorId, occurredAt, execution))
        { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.Invalid)); }
        if (!_capacity.TryEnter(out var scope, cancellationToken))
        { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.RequestId, out var previous))
            {
                return Task.FromResult(previous.Receipt.Matches(request, actorId) ? Result.Success(previous.Receipt)
                    : Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.RequestConflict));
            }
            var outbox = _readOutbox();
            outbox.TryGetValue(request.MessageId, out var entry);
            if (entry is not null && !_source.Manages(entry.EventName))
            { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.Unmanaged)); }
            var retried = entry?.RetryRevision == request.ExpectedRetryRevision
                ? entry.RetryDelivery(request.ExpectedDeadLetteredAt) : null;
            if (retried is null)
            { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.Conflict)); }
            var prepared = FactDeliveryRecoveryPreparation.Prepare(request, _source.Context, actorId, occurredAt, execution);
            var bytes = prepared.PayloadBytes;
            if (_receipts.Count >= _limits.MaxRecords || bytes > _limits.MaxRecordPayloadBytes || bytes > _limits.MaxPayloadBytes - _bytes)
            { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.Exhausted)); }
            var next = new Dictionary<Guid, StoredRecovery>(_receipts) { [request.RequestId] = new(prepared.Receipt, bytes) };
            cancellationToken.ThrowIfCancellationRequested();
            // The existing Guid key assignment cannot grow the owned dictionary; allocation is complete before publication.
            outbox[request.MessageId] = retried;
            _receipts = next;
            _bytes += bytes;
            return Task.FromResult(Result.Success(prepared.Receipt));
        }
    }

    /// <summary>读取保留的原裁决，不借用当前投递状态。</summary>
    /// <param name="requestId">稳定请求。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>原裁决或所属拒绝。</returns>
    public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_capacity.TryEnter(out var scope, cancellationToken))
        { return Task.FromResult(Result.Failure<FactDeliveryRecoveryReceipt>(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_receipts.TryGetValue(requestId, out var record) ? Result.Success(record.Receipt)
                : Result.Failure<FactDeliveryRecoveryReceipt>(_source.Errors.NotFound));
        }
    }

    /// <summary>稳定、有界、原子释放到期凭据及接受阶段记录的字节计量。</summary>
    /// <param name="batchSize">每轮一至一千个请求。</param>
    /// <param name="now">维护时钟给出的当前时刻。</param>
    /// <param name="cancellationToken">实际发布前可取消。</param>
    /// <returns>实际释放的请求数。</returns>
    public Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        { throw new InvalidOperationException("恢复凭据清理必须使用独立的维护作用域。"); }
        if (!_capacity.TryEnter(out var scope, cancellationToken)) { throw new CommittedFactCapacityBusyException(); }
        using (scope)
        {
            var candidates = _receipts.Values.Where(record => record.Receipt.RetainUntil <= now)
                .OrderBy(record => record.Receipt.RetainUntil).ThenBy(record => record.Receipt.RequestId).Take(batchSize).ToArray();
            if (candidates.Length == 0) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(0); }
            var bytes = candidates.Sum(record => (long)record.PayloadBytes);
            if (bytes > _bytes) { throw new InvalidOperationException("恢复凭据容量计量不一致。"); }
            var next = new Dictionary<Guid, StoredRecovery>(_receipts);
            foreach (var record in candidates) { next.Remove(record.Receipt.RequestId); }
            cancellationToken.ThrowIfCancellationRequested();
            _receipts = next;
            _bytes -= bytes;
            return Task.FromResult(candidates.Length);
        }
    }

    private sealed record StoredRecovery(FactDeliveryRecoveryReceipt Receipt, int PayloadBytes);
}
