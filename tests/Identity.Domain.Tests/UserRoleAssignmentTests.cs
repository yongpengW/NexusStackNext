using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// 角色归属（见 <c>docs/adr/0002-user-owns-its-role-assignments.md</c>）。
/// <para>
/// 重点是<b>"空操作不发事件"</b>这一条：权限缓存的失效路径应当只在集合真的变了时被触发。
/// 参照仓库的失效窗口最长有 10 小时，无谓的失效事件会让它更难推理。
/// </para>
/// </summary>
public sealed class UserRoleAssignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static User NewUser()
    {
        var hash = PasswordHash.Create(new string('a', 64)).Value;
        var user = User.Register(new UserId(1), UserName.Create("leo").Value, hash, Now);

        // 注册本身会发一条事件；这里只关心角色相关的。
        user.ClearDomainEvents();
        return user;
    }

    [Fact]
    public void NewUser_HasNoRoles()
    {
        Assert.Empty(NewUser().RoleIds);
    }

    [Fact]
    public void AssignRole_AddsItAndRaisesTheEvent()
    {
        var user = NewUser();

        Assert.True(user.AssignRole(new RoleId(7), Now).IsSuccess);

        Assert.Equal(7L, Assert.Single(user.RoleIds).Value);

        var changed = Assert.IsType<UserRolesChanged>(Assert.Single(user.DomainEvents));
        Assert.Equal(1, changed.RoleCount);
        Assert.Equal(1L, changed.UserId.Value);
    }

    [Fact]
    public void AssignRole_Twice_IsIdempotentAndSilent()
    {
        var user = NewUser();
        user.AssignRole(new RoleId(7), Now);
        user.ClearDomainEvents();

        Assert.True(user.AssignRole(new RoleId(7), Now).IsSuccess);

        Assert.Single(user.RoleIds);
        Assert.Empty(user.DomainEvents);
    }

    [Fact]
    public void RevokeRole_RemovesItAndRaisesTheEvent()
    {
        var user = NewUser();
        user.AssignRole(new RoleId(7), Now);
        user.ClearDomainEvents();

        Assert.True(user.RevokeRole(new RoleId(7), Now).IsSuccess);

        Assert.Empty(user.RoleIds);

        var changed = Assert.IsType<UserRolesChanged>(Assert.Single(user.DomainEvents));
        Assert.Equal(0, changed.RoleCount);
    }

    [Fact]
    public void RevokeRole_ForAnUnassignedRole_IsSilent()
    {
        var user = NewUser();

        Assert.True(user.RevokeRole(new RoleId(99), Now).IsSuccess);

        Assert.Empty(user.RoleIds);
        Assert.Empty(user.DomainEvents);
    }

    [Fact]
    public void RoleIds_AreOrderedForAssertions()
    {
        var user = NewUser();
        user.AssignRole(new RoleId(30), Now);
        user.AssignRole(new RoleId(10), Now);
        user.AssignRole(new RoleId(20), Now);

        Assert.Equal([10L, 20L, 30L], user.RoleIds.Select(static id => id.Value));
    }

    [Fact]
    public void NullRoleId_IsRejected()
    {
        var user = NewUser();

        Assert.Throws<ArgumentNullException>(() => user.AssignRole(null!, Now));
        Assert.Throws<ArgumentNullException>(() => user.RevokeRole(null!, Now));
    }

    [Fact]
    public void DisabledUser_CanStillHaveRolesAdjusted()
    {
        // 禁用只影响"能否登录"，不影响管理员调整他的角色——
        // 否则一个被误禁用的账号连角色都清不掉。
        var user = NewUser();
        user.Disable(Now);
        user.ClearDomainEvents();

        Assert.True(user.AssignRole(new RoleId(7), Now).IsSuccess);

        Assert.Single(user.RoleIds);
    }
}
