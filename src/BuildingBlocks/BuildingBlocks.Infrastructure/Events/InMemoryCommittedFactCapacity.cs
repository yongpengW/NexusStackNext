using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>在所属存储写锁内裁决完整批次；提交成功后更新占用，只计量本批载荷。</summary>
public sealed class InMemoryCommittedFactCapacity
{
    private readonly Lock _gate;
    private readonly string _eventName;
    private MemoryCommittedFactCapacityOptions _policy;
    private readonly TimeSpan _writeTimeout;
    private long _records;
    private long _bytes;

    /// <summary>为一个初始为空的上下文存储创建独立账本。</summary>
    /// <param name="gate">与业务发布、交付更新、清理共用的写锁。</param>
    /// <param name="eventName">本上下文事实契约；普通业务消息不计入。</param>
    /// <param name="policy">本上下文策略；省略时仍使用有限默认值。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    public InMemoryCommittedFactCapacity(Lock gate, string eventName, MemoryCommittedFactCapacityOptions? policy = null,
        CommittedFactCapacityWriteOptions? write = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _eventName = eventName;
        _policy = policy ?? new();
        _policy.Validate();
        var budget = write ?? new();
        budget.Validate();
        _writeTimeout = budget.Timeout;
    }

    /// <summary>有限、可取消地获取所属共用写锁；失败时不修改状态。</summary>
    /// <param name="scope">成功时必须在同线程释放的作用域。</param>
    /// <param name="cancellationToken">等待与取得锁时的调用者取消。</param>
    /// <returns>取得锁；false 表示争用预算耗尽。</returns>
    public bool TryEnter(out InMemoryCommittedFactWriteScope scope, CancellationToken cancellationToken = default)
        => InMemoryCommittedFactWriteLock.TryEnter(_gate, _writeTimeout, cancellationToken, out scope);

    /// <summary>为无 Result 的端口获取有限作用域；忙时抛出稳定的已知异常。</summary>
    /// <param name="cancellationToken">等待与取得锁时的调用者取消。</param>
    /// <returns>必须在同线程释放的作用域。</returns>
    public InMemoryCommittedFactWriteScope Enter(CancellationToken cancellationToken = default)
        => TryEnter(out var scope, cancellationToken) ? scope : throw new CommittedFactCapacityBusyException();

    /// <summary>在原锁内与所属控制证据共同替换额度，不清理任何业务事实。</summary>
    /// <param name="policy">经过校验的新额度。</param>
    /// <param name="publish">已经准备完毕的所属控制证据发布。</param>
    /// <param name="cancellationToken">开始发布之前可取消。</param>
    public void ChangePolicy(MemoryCommittedFactCapacityOptions policy, Action publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(publish);
        policy.Validate();
        using (Enter(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            publish();
            _policy = policy;
        }
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
        using (Enter(cancellationToken))
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
        using (Enter(cancellationToken))
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
