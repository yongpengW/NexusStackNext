using System.Diagnostics;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>同线程的事实存储锁作用域；不得跨异步挂起或转交给其他线程。</summary>
public readonly ref struct InMemoryCommittedFactWriteScope
{
    private readonly Lock? _gate;

    internal InMemoryCommittedFactWriteScope(Lock gate) => _gate = gate;

    /// <summary>释放本次获取，包括同线程重入的这一层。</summary>
    public void Dispose() => _gate?.Exit();
}

internal static class InMemoryCommittedFactWriteLock
{
    public static bool TryEnter(Lock gate, TimeSpan timeout, CancellationToken cancellationToken,
        out InMemoryCommittedFactWriteScope scope)
    {
        scope = default;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { return false; }
            if (!gate.TryEnter((int)Math.Min(25, Math.Ceiling(remaining.TotalMilliseconds)))) { continue; }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                scope = new(gate);
                return true;
            }
            catch
            {
                gate.Exit();
                throw;
            }
        }
    }
}
