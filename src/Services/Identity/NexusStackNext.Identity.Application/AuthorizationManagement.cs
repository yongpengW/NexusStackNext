using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Ids;

namespace NexusStackNext.Identity.Application;

/// <summary>角色的管理视图及条件写入版本。</summary>
/// <param name="RoleId">标识。</param>
/// <param name="Code">稳定编码。</param>
/// <param name="Name">展示名。</param>
/// <param name="MenuIds">有序授权菜单。</param>
/// <param name="Version">角色聚合版本。</param>
public sealed record RoleView(long RoleId, string Code, string Name, IReadOnlyList<long> MenuIds, long Version);

/// <summary>查询一个角色。</summary>
/// <param name="RoleId">标识。</param>
public sealed record GetRoleQuery(long RoleId) : IQuery<RoleView>, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [RoleId];
}

/// <summary>只暴露管理所需的角色状态。</summary>
/// <param name="roles">角色仓储。</param>
public sealed class GetRoleHandler(IRoleRepository roles) : IQueryHandler<GetRoleQuery, RoleView>
{
    /// <inheritdoc />
    public async Task<Result<RoleView>> HandleAsync(GetRoleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var role = (await roles.FindManyAsync([new RoleId(query.RoleId)], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        return role is null ? Result.Failure<RoleView>(AuthorizationManagementErrors.RoleNotFound)
            : Result.Success(new RoleView(role.Id.Value, role.Code.Value, role.Name.Value, role.GrantedMenuIds.Select(id => id.Value).ToArray(), role.Version));
    }
}

/// <summary>撤销一个用户的角色归属，不改变其会话版本。</summary>
/// <param name="UserId">用户。</param>
/// <param name="RoleId">要移除的角色，包括已经不存在的引用。</param>
/// <param name="ExpectedVersion">观察到的用户版本。</param>
public sealed record RevokeRoleCommand(long UserId, long RoleId, long ExpectedVersion) : ICommand, IIdentifiedRequest, IExpectedUserVersion
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => [UserId, RoleId];
}

/// <summary>只提交一个 User 的实际角色变化。</summary>
/// <param name="users">用户仓储。</param>
/// <param name="clock">时刻。</param>
/// <param name="transaction">提交后缓存失效。</param>
public sealed class RevokeRoleHandler(IUserRepository users, IClock clock, IdentityCommandTransaction transaction) : ICommandHandler<RevokeRoleCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(RevokeRoleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindAsync(new UserId(command.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null) { return Result.Failure(IdentityErrors.UserNotFound()); }
        if (user.Version != command.ExpectedVersion) { return Result.Failure(UserLifecycleErrors.Conflict); }
        var result = user.RevokeRole(new RoleId(command.RoleId), clock.UtcNow);
        if (result.IsSuccess && user.Version != command.ExpectedVersion) { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}

/// <summary>按角色版本整体替换菜单授权；不写任何受影响用户。</summary>
/// <param name="RoleId">角色。</param>
/// <param name="ExpectedVersion">观察到的角色版本。</param>
/// <param name="MenuIds">目标菜单集合；空集合撤销全部。</param>
public sealed record ReplaceRoleMenusCommand(long RoleId, long ExpectedVersion, IReadOnlyList<long>? MenuIds) : ICommand, IIdentifiedRequest
{
    /// <inheritdoc />
    public IReadOnlyList<long> Identifiers => MenuIds is null ? [RoleId] : [RoleId, .. MenuIds];
}

/// <summary>在占用事务前限制替换请求的形状及大小。</summary>
public sealed class ReplaceRoleMenusValidator : IRequestValidator<ReplaceRoleMenusCommand>
{
    /// <inheritdoc />
    public Result Validate(ReplaceRoleMenusCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedVersion <= 0) { return Result.Failure(new("identity.role.version_invalid", "必须提供正数的角色版本。")); }
        return request.MenuIds is { Count: <= 256 } ? Result.Success()
            : Result.Failure(new("identity.role.menus_invalid", "必须提供菜单集合，最多 256 项。"));
    }
}

/// <summary>授权管理的稳定拒绝。</summary>
public static class AuthorizationManagementErrors
{
    /// <summary>角色不存在。</summary>
    public static Error RoleNotFound { get; } = new("identity.role.not_found", "角色不存在。");
    /// <summary>菜单不存在，不能产生悬空授权。</summary>
    public static Error MenuNotFound { get; } = new("identity.menu.not_found", "菜单不存在。");
    /// <summary>角色已经改变。</summary>
    public static Error RoleConflict { get; } = new("identity.role.conflict", "角色授权已变化，请重新读取后再提交。");
}

/// <summary>校验引用后只改变角色聚合；空操作不生成新版本或失效。</summary>
/// <param name="roles">角色。</param>
/// <param name="trees">菜单存在性，仅读取。</param>
/// <param name="clock">时刻。</param>
/// <param name="transaction">提交。</param>
public sealed class ReplaceRoleMenusHandler(IRoleRepository roles, IMenuTreeRepository trees, IClock clock, IdentityCommandTransaction transaction)
    : ICommandHandler<ReplaceRoleMenusCommand>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(ReplaceRoleMenusCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var role = (await roles.FindManyAsync([new RoleId(command.RoleId)], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (role is null) { return Result.Failure(AuthorizationManagementErrors.RoleNotFound); }
        if (role.Version != command.ExpectedVersion) { return Result.Failure(AuthorizationManagementErrors.RoleConflict); }
        var target = command.MenuIds!.Select(id => new MenuId(id)).ToHashSet();
        if (target.Count != 0)
        {
            var tree = await trees.FindAsync(cancellationToken).ConfigureAwait(false);
            var existing = tree?.Nodes.Select(node => node.Id).ToHashSet() ?? [];
            if (!target.IsSubsetOf(existing)) { return Result.Failure(AuthorizationManagementErrors.MenuNotFound); }
        }
        var result = role.ReplaceGrants(target, clock.UtcNow);
        if (result.IsSuccess && role.Version != command.ExpectedVersion) { transaction.InvalidatePermissionsAfterCommit(); }
        return result;
    }
}
