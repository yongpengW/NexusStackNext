using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.TestSupport;

/// <summary>在权限失效端口记录已提交命令的通知，不替代业务存储。</summary>
public sealed class RecordingPermissionCache : IPermissionCache
{
    /// <summary>实际收到的失效次数。</summary>
    public int Invalidations { get; private set; }

    /// <inheritdoc />
    public void Invalidate() => Invalidations++;

    /// <inheritdoc />
    public Task<Result<PermissionKeySet>> GetAsync(UserId userId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("This command does not read cached permissions.");
}
