using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>
/// 把"这个用户是谁"翻译成"他能访问哪些端点"。
///
/// <para><b>这是一条四段链路</b>：
/// 用户 → 他的角色 → 角色被授予的菜单 → 那些菜单背后的端点 → 端点上的权限键。</para>
///
/// <para><b> pourquoi 预计算。</b>参照仓库已经验证过这个设计
/// （<c>UserContextCacheService.cs:96-119</c>）：登录与改角色时把权限收敛成一个哈希集合，
/// 鉴权时只做一次集合查找，而不是每请求查库。这里把同一条链路做成一个端口后面的模块，
/// 于是它可以在没有数据库的情况下被完整测试。</para>
///
/// <para><b>四级"没有权限"都返回空集合，而不是失败</b>：
/// 没有角色、角色没被授予任何菜单、菜单背后没有端点、账号被禁用——
/// 这些都是"合法的没有权限"，不是错误。调用方拿到空集合，由
/// <c>AccessPolicy</c> 按 fail-closed 的规则拒绝（ADR-0010）。
/// 只有"用户根本不存在"才是失败。</para>
///
/// <para>接口只有 <see cref="ReadAsync"/> 一个方法。四段链路、四种空值情形、
/// 以及"用户不存在"这条错误路径都在它后面——这是它深的地方。</para>
/// </summary>
/// <param name="users">用户仓储。</param>
/// <param name="roles">角色仓储。</param>
/// <param name="apiResources">API 资源仓储。</param>
public sealed class UserPermissionReader(
    IUserRepository users,
    IRoleRepository roles,
    IApiResourceRepository apiResources) : IPermissionSource
{
    /// <summary>读出用户当前有效的权限键集合。</summary>
    /// <param name="userId">用户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>权限键集合；用户不存在时为失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="userId"/> 为 <c>null</c>。</exception>
    public async Task<Result<PermissionKeySet>> ReadAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var user = await users.FindAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<PermissionKeySet>(new Error(
                "identity.user.not_found",
                $"用户不存在：{userId.Value}。"));
        }

        // 被禁用的账号一律没有权限。授权与"能不能登录"是两件事，
        // 但两者都要求账号可用——少判一处就会出现"禁用了他，他还能调接口"。
        if (!user.IsEnabled)
        {
            return Result.Success(PermissionKeySet.Empty);
        }

        if (user.RoleIds.Count == 0)
        {
            return Result.Success(PermissionKeySet.Empty);
        }

        var grantedRoles = await roles.FindManyAsync(user.RoleIds, cancellationToken).ConfigureAwait(false);

        var menuIds = grantedRoles
            .SelectMany(static role => role.GrantedMenuIds)
            .ToHashSet();

        if (menuIds.Count == 0)
        {
            return Result.Success(PermissionKeySet.Empty);
        }

        var resources = await apiResources
            .FindByMenuIdsAsync(menuIds, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(PermissionKeySet.From(
            resources.Select(static resource => resource.PermissionKey)));
    }
}
