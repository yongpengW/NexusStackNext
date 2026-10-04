using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.TestSupport;

/// <summary>在权限缓存端口注入一次可恢复的读取故障，正常读取与失效仍委托真实缓存。</summary>
/// <param name="inner">真实缓存；自身拥有来源适配器的作用域协议。</param>
public sealed class FaultingPermissionCache(IPermissionCache inner) : IPermissionCache
{
    /// <summary>非空时读取保留这个异常实例；清空后恢复真实缓存读取。</summary>
    public Exception? ReadFailure { get; set; }

    /// <inheritdoc />
    public Task<Result<PermissionKeySet>> GetAsync(UserId userId, CancellationToken cancellationToken = default) =>
        ReadFailure is { } error ? Task.FromException<Result<PermissionKeySet>>(error) : inner.GetAsync(userId, cancellationToken);

    /// <inheritdoc />
    public void Invalidate() => inner.Invalidate();
}
