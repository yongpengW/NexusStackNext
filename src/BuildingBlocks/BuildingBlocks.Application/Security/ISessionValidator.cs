namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>由身份上下文检查访问令牌是否仍属于有效会话；不可使用普通查询缓存代替权威状态。</summary>
public interface ISessionValidator
{
    /// <summary>检查用户存在、启用且会话版本与令牌一致；无效身份返回 false，来源故障不得放行。</summary>
    /// <param name="userId">令牌中的用户标识。</param>
    /// <param name="sessionVersion">令牌中的会话版本；缺失时拒绝。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话是否有效。</returns>
    Task<bool> IsCurrentAsync(string userId, long? sessionVersion, CancellationToken cancellationToken = default);
}
