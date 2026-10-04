using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>在所属存储写锁内裁决完整批次；提交成功后更新占用，只计量本批载荷。</summary>
public sealed class InMemoryCommittedFactCapacity
{
    private readonly Lock _gate;
    private readonly string _eventName;
    private readonly MemoryCommittedFactCapacityOptions _policy;
    private long _records;
    private long _bytes;

    /// <summary>为一个初始为空的上下文存储创建独立账本。</summary>
    /// <param name="gate">与业务发布、交付更新、清理共用的写锁。</param>
    /// <param name="eventName">本上下文事实契约；普通业务消息不计入。</param>
    /// <param name="policy">本上下文策略；省略时仍使用有限默认值。</param>
    public InMemoryCommittedFactCapacity(Lock gate, string eventName, MemoryCommittedFactCapacityOptions? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _eventName = eventName;
        _policy = policy ?? new();
        _policy.Validate();
    }

    /// <summary>读取与准入和清理一致的快照；争锁时立即报告不可用，不阻塞诊断请求。</summary>
    /// <param name="owner">装配代码声明的上下文。</param>
    /// <param name="cancellationToken">调用者取消。</param>
    /// <returns>单一账本快照或暂不可读。</returns>
    public Result<CommittedFactCapacitySnapshot> Read(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gate.TryEnter()) { return Result.Failure<CommittedFactCapacitySnapshot>(CommittedFactCapacityErrors.Unavailable); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Success(new CommittedFactCapacitySnapshot(owner, false, _policy.MaxRecords,
                _policy.MaxPayloadBytes, _policy.MaxRecordPayloadBytes, _records, _bytes));
        }
        finally { _gate.Exit(); }
    }

    /// <summary>整批准入后发布预备好的状态；拒绝、取消或发布前异常不消耗额度。</summary>
    /// <param name="batch">完整的新消息批次；正文和身份在提交后不可变。</param>
    /// <param name="commit">所属存储的原子发布；所有可能失败的构造必须在首次可见修改之前完成。</param>
    /// <param name="cancellationToken">发布之前可取消；已发布的结果不会因迟到取消被改写。</param>
    /// <returns>容量足够且提交完成；不足时不调用发布。</returns>
    public bool TryCommit(IReadOnlyCollection<OutboxEntry> batch, Action commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long records = 0;
            long bytes = 0;
            foreach (var entry in batch)
            {
                if (entry.EventName != _eventName) { continue; }
                var size = Encoding.UTF8.GetByteCount(entry.Payload);
                if (size > _policy.MaxRecordPayloadBytes || records >= _policy.MaxRecords - _records
                    || size > _policy.MaxPayloadBytes - _bytes - bytes) { return false; }
                records++;
                bytes += size;
            }
            cancellationToken.ThrowIfCancellationRequested();
            commit();
            _records += records;
            _bytes += bytes;
            return true;
        }
    }

    /// <summary>与已确认过期副本的实际删除共同释放额度；确认交付本身不得调用。</summary>
    /// <param name="batch">已经在同一写锁下选出的删除批次。</param>
    /// <param name="commit">所属字典的原子删除；失败不得留下部分删除。</param>
    /// <param name="cancellationToken">实际删除前可取消。</param>
    public void Remove(IReadOnlyCollection<OutboxEntry> batch, Action commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long records = 0;
            long bytes = 0;
            foreach (var entry in batch)
            {
                if (entry.EventName != _eventName) { continue; }
                records++;
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(entry.Payload));
            }
            if (records > _records || bytes > _bytes) { throw new InvalidOperationException("内存事实容量与删除批次不一致。"); }
            cancellationToken.ThrowIfCancellationRequested();
            commit();
            _records -= records;
            _bytes -= bytes;
        }
    }
}
