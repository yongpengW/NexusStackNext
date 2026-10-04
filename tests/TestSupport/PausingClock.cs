using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.TestSupport;

/// <summary>只暂停指定工作线程的下一次时间读取，其他调用者仍可读取固定时间。</summary>
/// <param name="now">固定的服务端时间。</param>
public sealed class PausingClock(DateTimeOffset now) : IClock, IDisposable
{
    private readonly ManualResetEventSlim _resume = new(false);
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _thread;

    /// <summary>指定读取已暂停；用来安排另一个公开操作与它重叠。</summary>
    public Task Paused => _paused.Task;

    /// <summary>在执行公开操作的线程上调用；只影响该线程下一次时间读取。</summary>
    public void PauseNextReadOnCurrentThread() => _thread = Environment.CurrentManagedThreadId;

    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            var thread = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref _thread, 0, thread) == thread)
            {
                _paused.TrySetResult();
                if (!_resume.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("测试时间读取未被释放。"); }
            }
            return now;
        }
    }

    /// <summary>释放暂停的读取；测试必须在 finally 中调用并等待操作结束。</summary>
    public void Resume() => _resume.Set();

    /// <inheritdoc />
    public void Dispose() => _resume.Dispose();
}
