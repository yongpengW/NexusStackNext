using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.Identity.Application.Tests;

/// <summary>
/// 权限投影：用户 → 角色 → 菜单 → 端点 → 权限键。
/// <para>
/// <b>四级"没有权限"都是空集合而不是失败</b>——它们是合法的"没有权限"，
/// 由 <c>AccessPolicy</c> 按 fail-closed 的规则拒绝（ADR-0010）。
/// 只有"用户根本不存在"才是失败。
/// </para>
/// </summary>
public sealed class UserPermissionReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const long UsersMenu = 10;
    private const long RolesMenu = 20;

    [Fact]
    public async Task UnknownUser_IsAFailure_NotAnEmptySet()
    {
        // "没有权限"与"这个人不存在"必须能区分开。
        var fixture = new Fixture();

        var result = await fixture.Reader.ReadAsync(new UserId(999));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user.not_found", result.Error.Code);
    }

    [Fact]
    public async Task UserWithoutRoles_HasNoPermissions()
    {
        var fixture = new Fixture();
        await fixture.AddUserAsync();

        var result = await fixture.Reader.ReadAsync(new UserId(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Count);
    }

    [Fact]
    public async Task RoleWithoutMenus_GrantsNothing()
    {
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        var role = await fixture.AddRoleAsync(7);
        fixture.Assign(1, role);

        var result = await fixture.Reader.ReadAsync(new UserId(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Count);
    }

    [Fact]
    public async Task MenusWithoutEndpoints_GrantNothing()
    {
        // "授予了一个菜单"与"那个菜单背后有端点"是两件事。
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        var role = await fixture.AddRoleAsync(7, UsersMenu);
        fixture.Assign(1, role);

        var result = await fixture.Reader.ReadAsync(new UserId(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Count);
    }

    [Fact]
    public async Task DisabledUser_LosesEveryPermission()
    {
        // 少判这一处就会出现"禁用了他，他还能调接口"。
        var fixture = new Fixture();
        await fixture.AddUserAsync(enabled: false);
        var role = await fixture.AddRoleAsync(7, UsersMenu);
        fixture.Assign(1, role);
        await fixture.AddResourceAsync(1, "/api/identity/users/{id}", "GET", UsersMenu);

        var result = await fixture.Reader.ReadAsync(new UserId(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Count);
    }

    [Fact]
    public async Task EndToEnd_ProjectionFeedsTheAuthorizationDecision()
    {
        // 这是整条链路的落点：**RBAC 判定核心第一次有真实输入。**
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        var role = await fixture.AddRoleAsync(7, UsersMenu);
        fixture.Assign(1, role);
        await fixture.AddResourceAsync(1, "/api/identity/users/{id}", "GET", UsersMenu);

        var keys = (await fixture.Reader.ReadAsync(new UserId(1))).Value;

        Assert.Equal(1, keys.Count);
        Assert.True(keys.Contains(PermissionKey.From("/api/identity/users/{id}", "GET")));

        // 被授予的端点 → 放行
        Assert.Equal(
            AccessDecision.Allowed,
            AccessPolicy.Decide(true, false, keys, PermissionKey.From("/api/identity/users/{id}", "GET"), AuthorizationMode.PermissionKey));

        // 没被授予的端点（同一个菜单下的另一个方法）→ 拒绝
        Assert.Equal(
            AccessDecision.Forbidden,
            AccessPolicy.Decide(true, false, keys, PermissionKey.From("/api/identity/users/{id}", "DELETE"), AuthorizationMode.PermissionKey));
    }

    [Fact]
    public async Task EndpointWithoutAMenu_IsNeverGrantedByMenuBasedAuthorization()
    {
        // 一个不对应菜单的端点**不会被任何基于菜单的授权覆盖**——这是刻意的，不是遗漏。
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        var role = await fixture.AddRoleAsync(7, UsersMenu);
        fixture.Assign(1, role);
        await fixture.AddResourceAsync(1, "/api/identity/internal/health", "GET", menuId: null);
        await fixture.AddResourceAsync(2, "/api/identity/users/{id}", "GET", UsersMenu);

        var keys = (await fixture.Reader.ReadAsync(new UserId(1))).Value;

        Assert.Equal(1, keys.Count);
        Assert.False(keys.Contains(PermissionKey.From("/api/identity/internal/health", "GET")));
    }

    [Fact]
    public async Task MultipleRoles_UnionTheirMenus()
    {
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        var users = await fixture.AddRoleAsync(7, UsersMenu);
        var roles = await fixture.AddRoleAsync(8, RolesMenu);
        fixture.Assign(1, users);
        fixture.Assign(1, roles);

        await fixture.AddResourceAsync(1, "/api/identity/users/{id}", "GET", UsersMenu);
        await fixture.AddResourceAsync(2, "/api/identity/roles/{id}", "GET", RolesMenu);

        var keys = (await fixture.Reader.ReadAsync(new UserId(1))).Value;

        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task SameMenuGrantedByTwoRoles_IsNotDuplicated()
    {
        // 集合的语义：两个角色都授予同一个菜单，权限键只出现一次。
        var fixture = new Fixture();
        await fixture.AddUserAsync();
        fixture.Assign(1, await fixture.AddRoleAsync(7, UsersMenu));
        fixture.Assign(1, await fixture.AddRoleAsync(8, UsersMenu));
        await fixture.AddResourceAsync(1, "/api/identity/users/{id}", "GET", UsersMenu);

        var keys = (await fixture.Reader.ReadAsync(new UserId(1))).Value;

        Assert.Equal(1, keys.Count);
    }

    [Fact]
    public async Task RevokingARole_TakesItsPermissionsAway()
    {
        var fixture = new Fixture();
        var user = await fixture.AddUserAsync();
        fixture.Assign(1, await fixture.AddRoleAsync(7, UsersMenu));
        await fixture.AddResourceAsync(1, "/api/identity/users/{id}", "GET", UsersMenu);

        Assert.Equal(1, (await fixture.Reader.ReadAsync(new UserId(1))).Value.Count);

        Assert.True(user.RevokeRole(new RoleId(7), Now).IsSuccess);

        Assert.Equal(0, (await fixture.Reader.ReadAsync(new UserId(1))).Value.Count);
    }

    [Fact]
    public async Task NullUserId_IsRejected()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Reader.ReadAsync(null!));
    }

    /// <summary>把三个内存适配器与一个读取器装在一起，测试里只关心链路。</summary>
    private sealed class Fixture
    {
        private readonly InMemoryUserRepository _users = new();
        private readonly InMemoryRoleRepository _roles = new();
        private readonly InMemoryApiResourceRepository _resources = new();
        private readonly Dictionary<long, User> _createdUsers = new();

        public Fixture() => Reader = new UserPermissionReader(_users, _roles, _resources);

        public UserPermissionReader Reader { get; }

        public async Task<User> AddUserAsync(long id = 1, bool enabled = true)
        {
            var hash = PasswordHash.Create(new string('a', 64)).Value;
            var user = User.Register(new UserId(id), UserName.Create($"user{id}").Value, hash, Now);

            if (!enabled)
            {
                user.Disable(Now);
            }

            await _users.AddAsync(user);
            _createdUsers[id] = user;
            return user;
        }

        public async Task<Role> AddRoleAsync(long id, params long[] menuIds)
        {
            var role = Role.Create(
                new RoleId(id),
                RoleCode.Create($"role-{id}").Value,
                RoleName.Create($"角色 {id}").Value);

            foreach (var menuId in menuIds)
            {
                role.Grant(new MenuId(menuId), Now);
            }

            await _roles.AddAsync(role);
            return role;
        }

        public async Task AddResourceAsync(long id, string route, string method, long? menuId)
        {
            var resource = ApiResource.Create(
                new ApiResourceId(id),
                RoutePattern.Create(route).Value,
                method,
                menuId is { } value ? new MenuId(value) : null).Value;

            await _resources.AddAsync(resource);
        }

        /// <summary>
        /// 同步分配角色。
        /// <para>刻意不写 <c>FindAsync(...).GetAwaiter().GetResult()</c>——
        /// 参照仓库的"构造体内 sync-over-async"是评审 04 F22 列出的可测试性问题之一，
        /// 自己的测试更不该犯。这里直接持有创建过的聚合实例。</para>
        /// </summary>
        /// <param name="userId">用户标识。</param>
        /// <param name="role">角色。</param>
        public void Assign(long userId, Role role) => _createdUsers[userId].AssignRole(role.Id, Now);
    }
}
