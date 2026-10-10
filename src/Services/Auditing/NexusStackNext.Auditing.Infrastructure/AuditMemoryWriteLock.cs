using System.Diagnostics;
using NexusStackNext.Auditing.Application;

namespace NexusStackNext.Auditing.Infrastructure;

internal static class AuditMemoryWriteLock
{
    public static Scope Enter(Lock gate, int milliseconds, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = milliseconds - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) { throw new AuditStorageUnavailableException(false); }
            if (!gate.TryEnter((int)Math.Min(25, Math.Ceiling(remaining)))) { continue; }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new Scope(gate);
            }
            catch { gate.Exit(); throw; }
        }
    }

    internal readonly ref struct Scope(Lock gate)
    {
        public void Dispose() => gate.Exit();
    }
}
