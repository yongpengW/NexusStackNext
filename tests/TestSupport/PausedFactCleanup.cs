using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.TestSupport;

/// <summary>通过真实清理与外部时钟暂停安排争用，集中维护持有任务的释放与回收。</summary>
public sealed class PausedFactCleanup
{
    private readonly PausingClock _clock;
    private readonly Task<int> _running;

    /// <summary>启动一次真实清理；调用方必须在 finally 中等待 ReleaseAsync。</summary>
    /// <param name="clock">由清理使用的外部时钟。</param>
    /// <param name="cleanup">所属上下文的真实清理端口。</param>
    public PausedFactCleanup(PausingClock clock, ICommittedFactCleanup cleanup)
    {
        _clock = clock;
        // 夹具会同步暂停；不要占用负责调用者继续执行和定时取消的线程池。
        _running = Task.Factory.StartNew(() =>
        {
            clock.PauseNextReadOnCurrentThread();
            return cleanup.CleanupAsync();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>有界等待清理进入外部时间读取；超时仍须释放并回收持有任务。</summary>
    /// <returns>清理已暂停。</returns>
    public Task WaitUntilPausedAsync() => _clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>释放并等待持有任务退出；重复调用保持原清理结果或异常。</summary>
    /// <returns>实际清理条数。</returns>
    public async Task<int> ReleaseAsync()
    {
        _clock.Resume();
        return await _running.ConfigureAwait(false);
    }
}
