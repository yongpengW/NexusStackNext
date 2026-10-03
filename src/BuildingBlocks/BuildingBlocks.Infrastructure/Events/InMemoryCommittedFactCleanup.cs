using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>在所属存储的写锁下清理事实副本，保留业务状态与其他消息。</summary>
/// <param name="gate">业务提交和交付更新共用的写锁。</param>
/// <param name="entries">取得当前已提交字典；仅在持锁期间调用，支持原子替换的存储。</param>
/// <param name="eventName">模块声明的事实契约。</param>
/// <param name="options">本上下文清理策略。</param>
/// <param name="clock">服务端时钟。</param>
public sealed class InMemoryCommittedFactCleanup(Lock gate, Func<Dictionary<Guid, OutboxEntry>> entries,
    string eventName, CommittedFactCleanupOptions options, IClock clock) : ICommittedFactCleanup
{
    /// <inheritdoc />
    public Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (eventName.Length > 200) { throw new InvalidOperationException("事实事件名称超出允许长度。"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("事实副本清理必须使用独立的维护作用域。");
        }
        while (!gate.TryEnter(25)) { cancellationToken.ThrowIfCancellationRequested(); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = entries();
            var cutoff = clock.UtcNow.Subtract(options.DeliveredRetention);
            var candidates = current.Values.Where(entry => entry.EventName == eventName && entry.DeliveredAt <= cutoff && entry.DeadLetteredAt is null)
                .OrderBy(entry => entry.DeliveredAt).ThenBy(entry => entry.Id).Take(options.BatchSize).Select(entry => entry.Id).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in candidates) { current.Remove(id); }
            return Task.FromResult(candidates.Length);
        }
        finally { gate.Exit(); }
    }
}
