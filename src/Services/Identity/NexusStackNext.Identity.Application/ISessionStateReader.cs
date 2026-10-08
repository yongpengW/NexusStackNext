using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>会话授权需要的最小当前用户事实，不包含密码、角色或刷新凭据。</summary>
/// <param name="SessionVersion">持久会话版本。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="IsRoot">是否为内置身份。</param>
public sealed record SessionState(long SessionVersion, bool IsEnabled, bool IsRoot);

/// <summary>读取 Identity 自己的当前已提交状态；不可由查询缓存或跟踪实体替代。</summary>
public interface ISessionStateReader
{
    /// <summary>以有限预算读取，用户不存在返回空状态，来源故障返回失败。</summary>
    /// <param name="userId">经过验证的主体标识。</param>
    /// <param name="cancellationToken">本次调用取消。</param>
    /// <returns>最小状态或来源不可用。</returns>
    Task<Result<SessionState?>> ReadAsync(long userId, CancellationToken cancellationToken = default);
}
