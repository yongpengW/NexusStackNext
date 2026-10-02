using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>一次异步调用链的操作身份；并发调用链各自持有自己的值。</summary>
internal sealed class OperationExecutionContext
{
    private readonly AsyncLocal<Scope?> _current = new();

    public ExecutionOrigin? Origin => _current.Value is { IsActive: true } scope ? scope.Origin : null;
    public Guid? Current => Origin?.OperationId;
    public bool IsSuppressed => _current.Value is { IsActive: true, IsSuppressed: true };
    public bool IsSystem => _current.Value is { IsActive: true, IsSystem: true };

    public Scope Enter(ExecutionOrigin? origin, bool suppressed = false, bool system = false)
    {
        var scope = new Scope(this, _current.Value, origin, suppressed, system);
        _current.Value = scope;
        return scope;
    }

    internal sealed class Scope(OperationExecutionContext owner, Scope? parent, ExecutionOrigin? origin, bool suppressed, bool system) : IDisposable
    {
        private int _closed;
        public ExecutionOrigin? Origin { get; } = origin;
        public bool IsSuppressed { get; } = suppressed;
        public bool IsSystem { get; } = system;
        public bool IsActive => Volatile.Read(ref _closed) == 0;

        public void Dispose()
        {
            // 子异步流持有同一 frame；父操作结束后，继承的旧 frame 也不再有效。
            if (Interlocked.Exchange(ref _closed, 1) != 0) { return; }
            owner._current.Value = parent;
        }
    }
}
