using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.TestSupport;

/// <summary>在真实计划来源读取前暂停一次，读取结束后释放外部争用；不改变存储结果。</summary>
/// <param name="inner">真实计划存储。</param>
/// <param name="afterRead">真实读取结束后的外部资源释放。</param>
public sealed class PausingScheduleOriginStore(IScheduledTaskStore inner, Action afterRead) : IScheduledTaskStore
{
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    /// <summary>到期扫描已返回，首次来源读取尚未开始。</summary>
    public Task Paused => _paused.Task;

    /// <summary>允许真实来源读取继续。</summary>
    public void Resume() => _resume.TrySetResult();

    /// <inheritdoc />
    public async Task<ExecutionOrigin?> ReadExecutionOriginAsync(ScheduledTaskId id, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) != 1) { return await inner.ReadExecutionOriginAsync(id, cancellationToken); }
        _paused.TrySetResult();
        try
        {
            await _resume.Task.WaitAsync(cancellationToken);
            return await inner.ReadExecutionOriginAsync(id, cancellationToken);
        }
        finally { afterRead(); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
        => inner.ReadDueAsync(now, batchSize, cancellationToken);
    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    /// <inheritdoc />
    public Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(page, limit, cancellationToken);
    /// <inheritdoc />
    public Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
    /// <inheritdoc />
    public Task<Result> AddAsync(ScheduledTask task, ExecutionOrigin? origin = null, CancellationToken cancellationToken = default) => inner.AddAsync(task, origin, cancellationToken);
    /// <inheritdoc />
    public Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default) => inner.SaveAsync(task, expectedVersion, cancellationToken);
    /// <inheritdoc />
    public Task<Result> RecordDecisionAsync(ScheduledTask task, long expectedVersion, ScheduleDecision decision, ScheduleOccurrence? occurrence, CancellationToken cancellationToken = default)
        => inner.RecordDecisionAsync(task, expectedVersion, decision, occurrence, cancellationToken);
    /// <inheritdoc />
    public Task<ScheduleDecisionPage> ReadDecisionsAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default) => inner.ReadDecisionsAsync(planId, offset, limit, cancellationToken);
    /// <inheritdoc />
    public Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default) => inner.ReadOccurrencesAsync(planId, offset, limit, cancellationToken);
    /// <inheritdoc />
    public Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default)
        => inner.RetryOccurrenceAsync(occurrenceId, expectedDeadLetteredAt, cancellationToken);
}
