namespace NexusStackNext.TestSupport;

/// <summary>用独立调用者线程安排一次取消，不依赖被真实同步等待占用的线程池。</summary>
public sealed class TimedCallerCancellation : IAsyncDisposable
{
    private readonly CancellationTokenSource _source = new();
    private Task? _scheduled;

    /// <summary>真实传给公开操作的调用者令牌。</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>调用者是否已经发出取消。</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>在操作启动时安排一次取消；同一装置不能重复安排。</summary>
    /// <param name="delay">取消前的有限等待。</param>
    public void CancelAfter(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (_scheduled is not null) { throw new InvalidOperationException("测试调用者取消只能安排一次。"); }
        _scheduled = Task.Factory.StartNew(() =>
        {
            Thread.Sleep(delay);
            _source.Cancel();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>等待取消线程及其回调退出，再释放令牌来源；不丢弃后台任务。</summary>
    /// <returns>全部所属工作已退出。</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_scheduled is not null) { await _scheduled.ConfigureAwait(false); }
        }
        finally { _source.Dispose(); }
    }
}
