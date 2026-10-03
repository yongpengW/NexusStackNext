using System.Diagnostics.Metrics;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Application;

/// <summary>来源宿主的操作记录日志；持久化使用与业务事务无关的独立事务。</summary>
public interface IOperationJournal
{
    /// <summary>保存一个不可变观察阶段及其待投递消息；同身份同内容重复成功，不同内容拒绝。</summary>
    /// <param name="observation">来源已经观察到的请求阶段，不是业务提交声明。</param>
    /// <param name="cancellationToken">日志自身的有界取消令牌。</param>
    /// <returns>已保存或重复；身份冲突、非法记录返回失败，存储故障抛出。</returns>
    Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default);
}

/// <summary>本进程未能登记的操作日志计数；后续成功不清除已发生的缺失。</summary>
public sealed class OperationJournalStatus
{
    private static readonly Meter Meter = new("NexusStackNext.OperationJournal", "1.0.0");
    private static readonly Counter<long> FailedWrites = Meter.CreateCounter<long>("operation_journal.write_failures", "{failure}");
    private long _failureCount;
    private static readonly Counter<long> FailedCleanups = Meter.CreateCounter<long>("operation_journal.cleanup_failures", "{failure}");
    private long _cleanupFailures;
    private int _cleanupDegraded;

    /// <summary>当前进程内的维护失败次数；不同于未保存观察的计数。</summary>
    public long CleanupFailureCount => Interlocked.Read(ref _cleanupFailures);

    /// <summary>最近一次清理失败；后续成功可恢复，但不能清除观察缺失。</summary>
    public bool CleanupDegraded => Volatile.Read(ref _cleanupDegraded) != 0;

    /// <summary>记录维护故障，不把它误计为丢失操作观察。</summary>
    public void ReportCleanupFailure()
    {
        Interlocked.Increment(ref _cleanupFailures);
        Volatile.Write(ref _cleanupDegraded, 1);
        try { FailedCleanups.Add(1); }
        catch (Exception) { /* 外部指标监听器故障不改变维护结论。 */ }
    }

    /// <summary>本轮清理完成，解除当前维护降级；历史失败计数保留。</summary>
    public void ReportCleanupSuccess() => Volatile.Write(ref _cleanupDegraded, 0);

    /// <summary>当前进程运行以来的登记失败次数，重启后从零开始。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>登记一次失败；调用方仍须返回原业务结果，不能声称这条日志已保存。</summary>
    public void ReportFailure()
    {
        Interlocked.Increment(ref _failureCount);
        try { FailedWrites.Add(1); }
        catch (Exception) { /* 外部指标监听器故障不影响业务，也不抹掉本地失败计数。 */ }
    }
}
