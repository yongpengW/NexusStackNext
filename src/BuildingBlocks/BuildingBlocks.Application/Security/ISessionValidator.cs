using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Security;

/// <summary>本次权威读取已经确认的会话；根身份来自当前用户状态。</summary>
/// <param name="IsRoot">当前权威根身份。</param>
public sealed record ValidatedSession(bool IsRoot);

/// <summary>会话拒绝与权威来源不可用的稳定分类。</summary>
public static class SessionValidationErrors
{
    /// <summary>会话无效、不存在、禁用或已经撤销。</summary>
    public static readonly Error Invalid = new("identity.session.invalid", "当前会话已失效。");
    /// <summary>权威来源无法在本次预算内提供可信判定。</summary>
    public static readonly Error Unavailable = new("identity.session.unavailable", "会话权威来源暂时不可用。");
}

/// <summary>由身份上下文检查访问令牌是否仍属于有效会话；不可使用普通查询缓存代替权威状态。</summary>
public interface ISessionValidator
{
    /// <summary>检查用户存在、启用及版本一致，并读取当前根身份；失败不允许回退到声明或缓存。</summary>
    /// <param name="userId">令牌中的用户标识。</param>
    /// <param name="sessionVersion">令牌中的会话版本；缺失时拒绝。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前会话事实、认证拒绝或来源不可用。</returns>
    Task<Result<ValidatedSession>> ValidateAsync(string userId, long? sessionVersion, CancellationToken cancellationToken = default);
}
