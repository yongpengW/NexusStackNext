using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.BuildingBlocks.Application.Authorization;

/// <summary>
/// 一次请求的授权模式。
/// <para>
/// <b><see cref="DenyAll"/> 是 0，也就是默认值</b>——这是刻意的：配置缺失、枚举未赋值、
/// 反序列化失败，任何一条路径都会落到"拒绝"而不是"放行"。
/// </para>
/// <para>
/// 参照仓库反着来：<c>ApiAuthorizationOptions.cs:21</c> 默认 <c>RootOnly</c>，
/// 而 <c>RequestAuthorizeFilter.cs:87-111</c> 在配置缺失时是 <b>fail-open</b> 的。
/// 一次配置事故就等于全站放开。
/// </para>
/// </summary>
public enum AuthorizationMode
{
    /// <summary>一律拒绝。<b>默认值</b>，也是配置缺失时的行为。</summary>
    DenyAll = 0,

    /// <summary>只允许内置根管理员。</summary>
    RootOnly,

    /// <summary>任何已认证用户都可以。</summary>
    Authenticated,

    /// <summary>必须持有该端点声明的权限键。</summary>
    PermissionKey,
}

/// <summary>授权判定的结果。</summary>
public enum AccessDecision
{
    /// <summary>放行。</summary>
    Allowed,

    /// <summary>未认证 → 401。</summary>
    Unauthenticated,

    /// <summary>已认证但无权 → 403。</summary>
    Forbidden,
}

/// <summary>
/// 授权判定。
/// <para>
/// <b>纯函数，因此"默认拒绝"这件事可以被穷举验证</b>，而不是靠读配置代码来相信它。
/// 认证在网关、授权在上下文（ADR-0003）——这个类就是各上下文共用的那一小段判定。
/// </para>
/// </summary>
public static class AccessPolicy
{
    /// <summary>判定一次请求。</summary>
    /// <param name="isAuthenticated">请求是否已通过认证。</param>
    /// <param name="isRoot">是否为内置根管理员。</param>
    /// <param name="granted">该用户被授予的权限键集合；未知时为 <c>null</c>。</param>
    /// <param name="required">该端点声明的权限键；未声明时为 <c>null</c>。</param>
    /// <param name="mode">授权模式。</param>
    /// <returns>判定结果。</returns>
    public static AccessDecision Decide(
        bool isAuthenticated,
        bool isRoot,
        PermissionKeySet? granted,
        PermissionKey? required,
        AuthorizationMode mode)
    {
        // 未认证与模式无关：先答 401，不要用 403 掩盖"你还没登录"。
        if (!isAuthenticated)
        {
            return AccessDecision.Unauthenticated;
        }

        return mode switch
        {
            AuthorizationMode.DenyAll => AccessDecision.Forbidden,
            AuthorizationMode.RootOnly => isRoot ? AccessDecision.Allowed : AccessDecision.Forbidden,
            AuthorizationMode.Authenticated => AccessDecision.Allowed,

            // 根管理员绕过权限键：它是"能在系统里做任何事"的角色，逐条授予没有意义。
            AuthorizationMode.PermissionKey when isRoot => AccessDecision.Allowed,

            // 端点没声明要求就是拒绝：公开端点应当走 Authenticated 模式显式表达，
            // 而不是靠"忘了标注"这种默认放行。
            AuthorizationMode.PermissionKey when required is null => AccessDecision.Forbidden,

            AuthorizationMode.PermissionKey when granted is not null && granted.Contains(required.Value) =>
                AccessDecision.Allowed,

            // 未知的枚举值也会落到这里——fail-closed 的最后一道。
            _ => AccessDecision.Forbidden,
        };
    }
}
