using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// 角色聚合。核心是"系统内置角色不允许删除"这条不变量<b>住在聚合里</b>，
/// 以及权限变更只在集合真的变化时发事件。
/// </summary>
public sealed class RoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static Role NewRole(bool system = false) => system
        ? Role.CreateSystem(new RoleId(1), RoleCode.Create("sys.admin").Value, RoleName.Create("系统管理员").Value, Platform.Admin)
        : Role.Create(new RoleId(2), RoleCode.Create("ops").Value, RoleName.Create("运维").Value);

    private static MenuId Menu(long id) => new(id);

    [Fact]
    public void CreateSystem_MarksRoleAsSystem()
    {
        Assert.True(NewRole(system: true).IsSystem);
        Assert.False(NewRole().IsSystem);
    }

    [Fact]
    public void EnsureDeletable_OnSystemRole_ThrowsDomainException()
    {
        // 参照仓库把这条写在 RoleService.cs:32-35 的 if 里，换个调用方就能绕过去。
        var role = NewRole(system: true);

        var exception = Assert.Throws<DomainException>(role.EnsureDeletable);

        Assert.Contains("系统内置角色不允许删除", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDeletable_OnRegularRole_DoesNotThrow()
    {
        NewRole().EnsureDeletable();
    }

    [Fact]
    public void Grant_NewMenu_RaisesPermissionsChanged()
    {
        var role = NewRole();

        Assert.True(role.Grant(Menu(10), Now).IsSuccess);

        Assert.Equal([10L], role.GrantedMenuIds.Select(static id => id.Value));
        var changed = Assert.IsType<RolePermissionsChanged>(Assert.Single(role.DomainEvents));
        Assert.Equal(1, changed.GrantedMenuCount);
    }

    [Fact]
    public void Grant_SameMenuTwice_RaisesEventOnlyOnce()
    {
        // 空操作发事件会让缓存无谓失效——参照仓库的失效路径本就最长有 10 小时窗口。
        var role = NewRole();
        role.Grant(Menu(10), Now);
        role.ClearDomainEvents();

        Assert.True(role.Grant(Menu(10), Now).IsSuccess);

        Assert.Empty(role.DomainEvents);
        Assert.Single(role.GrantedMenuIds);
    }

    [Fact]
    public void Revoke_NotGranted_DoesNotRaise()
    {
        var role = NewRole();

        Assert.True(role.Revoke(Menu(99), Now).IsSuccess);

        Assert.Empty(role.DomainEvents);
    }

    [Fact]
    public void Revoke_Granted_RemovesAndRaises()
    {
        var role = NewRole();
        role.Grant(Menu(10), Now);
        role.Grant(Menu(11), Now);
        role.ClearDomainEvents();

        Assert.True(role.Revoke(Menu(10), Now).IsSuccess);

        Assert.Equal([11L], role.GrantedMenuIds.Select(static id => id.Value));
        Assert.IsType<RolePermissionsChanged>(Assert.Single(role.DomainEvents));
    }

    [Fact]
    public void ReplaceGrants_SameSet_DoesNotRaise()
    {
        var role = NewRole();
        role.ReplaceGrants([Menu(1), Menu(2)], Now);
        role.ClearDomainEvents();

        Assert.True(role.ReplaceGrants([Menu(2), Menu(1)], Now).IsSuccess);

        Assert.Empty(role.DomainEvents);
    }

    [Fact]
    public void ReplaceGrants_DifferentSet_ReplacesExactly()
    {
        var role = NewRole();
        role.ReplaceGrants([Menu(1), Menu(2), Menu(3)], Now);
        role.ClearDomainEvents();

        Assert.True(role.ReplaceGrants([Menu(3), Menu(4)], Now).IsSuccess);

        Assert.Equal([3L, 4L], role.GrantedMenuIds.Select(static id => id.Value));
        Assert.IsType<RolePermissionsChanged>(Assert.Single(role.DomainEvents));
    }

    [Fact]
    public void GrantedMenuIds_AreOrdered_SoAssertionsAreStable()
    {
        var role = NewRole();
        role.Grant(Menu(30), Now);
        role.Grant(Menu(10), Now);
        role.Grant(Menu(20), Now);

        Assert.Equal([10L, 20L, 30L], role.GrantedMenuIds.Select(static id => id.Value));
    }

    [Fact]
    public void Rename_And_ChangePlatforms_UpdateState()
    {
        var role = NewRole();

        role.Rename(RoleName.Create("新名字").Value);
        role.ChangePlatforms(Platform.Pc | Platform.Pos);

        Assert.Equal("新名字", role.Name.Value);
        Assert.Equal(Platform.Pc | Platform.Pos, role.Platforms);
    }

    [Fact]
    public void Platform_AllIsNotNone()
    {
        // 参照仓库写的是 All = 0，于是"全部"与"没有"是同一个值，
        // 在一处被当作全部、在另一处被当作没有，权限判定会静默失效。
        Assert.NotEqual(Platform.None, Platform.All);
        Assert.Equal(Platform.Admin | Platform.Pc | Platform.MiniProgram | Platform.Pos, Platform.All);
        Assert.True(Platform.All.HasFlag(Platform.Admin));
    }
}
