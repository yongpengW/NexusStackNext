using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.ApiResources;

/// <summary>
/// API 资源：一个可被授权的端点。
/// <para>
/// 它存在的意义是把"权限"落到具体端点上。<see cref="PermissionKey"/> 的格式
/// <c>路由模板:HTTP方法</c> 沿用参照仓库已验证的预计算设计：登录/改角色时算出用户的权限键集合，
/// 鉴权时只做一次哈希集合查找，而不是每请求查库。
/// </para>
/// <para>把格式收在值对象里，是为了让大小写与边界只有一处定义——散在各处迟早会不一致，
/// 而不一致的后果是"某些权限静默失效"。</para>
/// </summary>
public sealed class ApiResource : AggregateRoot<ApiResourceId>
{
    private static readonly HashSet<string> AllowedMethods =
        new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.Ordinal);

    private ApiResource(ApiResourceId id, RoutePattern routePattern, string httpMethod, MenuId? menuId)
        : base(id)
    {
        RoutePattern = routePattern;
        HttpMethod = httpMethod;
        MenuId = menuId;
    }

    /// <summary>路由模板。</summary>
    public RoutePattern RoutePattern { get; }

    /// <summary>HTTP 方法（大写）。</summary>
    public string HttpMethod { get; }

    /// <summary>
    /// 该端点所属的菜单；<c>null</c> 表示它不对应任何菜单。
    /// <para>
    /// <b>权限投影靠它把两端接起来</b>：角色被授予的是菜单，而鉴权需要的是端点的权限键。
    /// 没有这条连接，"授予了一个菜单"就无法翻译成"能访问哪些端点"。
    /// </para>
    /// <para>
    /// 一个端点不对应菜单是合法的（例如只有后台会调的接口），
    /// 但它因此**不会被任何基于菜单的授权覆盖**——那是刻意的，不是遗漏。
    /// </para>
    /// </summary>
    public MenuId? MenuId { get; }

    /// <summary>鉴权时使用的权限键。</summary>
    public PermissionKey PermissionKey => RoutePattern.ToPermissionKey(HttpMethod);

    /// <summary>创建一个 API 资源。</summary>
    /// <param name="id">标识。</param>
    /// <param name="routePattern">路由模板。</param>
    /// <param name="httpMethod">HTTP 方法。</param>
    /// <param name="menuId">所属菜单；<c>null</c> 表示不对应菜单。</param>
    /// <returns>成功时返回资源；方法不在允许集合内则失败。</returns>
    public static Result<ApiResource> Create(
        ApiResourceId id,
        RoutePattern routePattern,
        string? httpMethod,
        MenuId? menuId = null)
    {
        ArgumentNullException.ThrowIfNull(routePattern);

        var method = httpMethod?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(method) || !AllowedMethods.Contains(method))
        {
            return Result.Failure<ApiResource>(new Error(
                "identity.api_resource.method_not_allowed",
                $"不支持的 HTTP 方法：{httpMethod}。"));
        }

        return Result.Success(new ApiResource(id, routePattern, method, menuId));
    }
}
