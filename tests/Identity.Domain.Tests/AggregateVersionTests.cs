using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// Identity 五个聚合的版本号（ADR-0011）。
/// <para>
/// 每个聚合的**改变路径**与**空操作路径**各覆盖一次——
/// 因为"空操作不得自增"这条契约编译器管不了。
/// </para>
/// </summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ValidSecret = new('a', 64);

    private static User NewUser(bool enabled = true)
    {
        var user = User.Register(
            new UserId(1),
            UserName.Create("leo").Value,
            PasswordHash.Create(ValidSecret).Value,
            Now);

        if (!enabled)
        {
            user.Disable(Now);
            user.ClearDomainEvents();
        }

        return user;
    }

    private static Role NewRole() =>
        Role.Create(new RoleId(1), RoleCode.Create("editor").Value, RoleName.Create("编辑").Value);

    private static MenuTree NewTree()
    {
        var tree = MenuTree.Create(new MenuTreeId(1));
        tree.AddRoot(new MenuId(1), MenuTitle.Create("根").Value);
        return tree;
    }

    // ---------- User ----------

    [Fact]
    public void NewUser_StartsAtOne()
    {
        Assert.Equal(1, NewUser().Version);
    }

    [Fact]
    public void User_ChangingPassword_Bumps()
    {
        var user = NewUser();

        Assert.True(user.ChangePassword(PasswordHash.Create(new string('b', 64)).Value, Now).IsSuccess);

        Assert.Equal(2, user.Version);
    }

    [Fact]
    public void User_SamePassword_IsANoOp()
    {
        var user = NewUser();
        var hash = PasswordHash.Create(new string('b', 64)).Value;
        user.ChangePassword(hash, Now);
        var before = user.Version;

        Assert.True(user.ChangePassword(hash, Now).IsFailure);

        Assert.Equal(before, user.Version);
    }

    [Fact]
    public void User_SameContact_IsANoOp()
    {
        var user = NewUser();
        var email = EmailAddress.Create("leo@example.com").Value;
        user.SetContact(email, null);
        var before = user.Version;

        user.SetContact(email, null);

        Assert.Equal(before, user.Version);
    }

    [Fact]
    public void User_DisableAndEnable_BumpOnce_RepeatsDoNot()
    {
        var user = NewUser();

        user.Disable(Now);
        Assert.Equal(2, user.Version);

        user.Disable(Now);
        Assert.Equal(2, user.Version);

        user.Enable(Now);
        Assert.Equal(3, user.Version);

        user.Enable(Now);
        Assert.Equal(3, user.Version);
    }

    [Fact]
    public void User_AssigningTheSameRoleTwice_BumpsOnce()
    {
        var user = NewUser();
        var roleId = new RoleId(7);

        user.AssignRole(roleId, Now);
        Assert.Equal(2, user.Version);

        user.AssignRole(roleId, Now);
        Assert.Equal(2, user.Version);

        Assert.Equal(2, user.Version);
        user.RevokeRole(roleId, Now);
        Assert.Equal(3, user.Version);
    }

    [Fact]
    public void User_LoginRecords_Bump()
    {
        var user = NewUser();

        user.RecordSuccessfulLogin(Now);
        Assert.Equal(2, user.Version);

        user.RecordFailedLogin(Now, LockoutPolicy.Default);
        Assert.Equal(3, user.Version);
    }

    // ---------- Role ----------

    [Fact]
    public void Role_SameName_IsANoOp()
    {
        var role = NewRole();
        var name = RoleName.Create("新名字").Value;
        role.Rename(name);
        var before = role.Version;

        role.Rename(name);

        Assert.Equal(before, role.Version);
    }

    [Fact]
    public void Role_GrantingTheSameMenuTwice_BumpsOnce()
    {
        var role = NewRole();

        role.Grant(new MenuId(10), Now);
        Assert.Equal(2, role.Version);

        role.Grant(new MenuId(10), Now);
        Assert.Equal(2, role.Version);
    }

    [Fact]
    public void Role_ReplaceGrantsWithTheSameSet_IsANoOp()
    {
        var role = NewRole();
        role.Grant(new MenuId(10), Now);
        var before = role.Version;

        role.ReplaceGrants([new MenuId(10)], Now);

        Assert.Equal(before, role.Version);
    }

    // ---------- MenuTree ----------

    [Fact]
    public void MenuTree_AddMoveRemoveUpdate_Bump()
    {
        var tree = NewTree();
        Assert.Equal(2, tree.Version); // Create=1, AddRoot=2

        tree.AddChild(new MenuId(1), new MenuId(2), MenuTitle.Create("子").Value);
        Assert.Equal(3, tree.Version);

        tree.Update(new MenuId(2), MenuTitle.Create("改了").Value, 1);
        Assert.Equal(4, tree.Version);

        tree.Remove(new MenuId(2));
        Assert.Equal(5, tree.Version);
    }

    [Fact]
    public void MenuTree_MovingToTheSamePlace_IsANoOp()
    {
        var tree = NewTree();
        tree.AddChild(new MenuId(1), new MenuId(2), MenuTitle.Create("子").Value);
        var before = tree.Version;

        // 2 已经在 1 下面，再移动一次位置没变。
        Assert.True(tree.Move(new MenuId(2), new MenuId(1)).IsSuccess);

        Assert.Equal(before, tree.Version);
    }

    /// <summary>
    /// 标题与排序都没变时，<c>Update</c> 是**空操作**（ADR-0011："Version 改变，
    /// 当且仅当可观察状态改变了"）。缺了这一条，一次什么都没改的保存会把聚合标成已修改，
    /// 于是乐观并发会在没有冲突的情况下误报冲突。
    /// </summary>
    [Fact]
    public void MenuTree_UpdatingWithTheSameContent_IsANoOp()
    {
        var tree = NewTree();
        tree.AddChild(new MenuId(1), new MenuId(2), MenuTitle.Create("子").Value);

        var node = Assert.Single(tree.ChildrenOf(new MenuId(1)));
        var before = tree.Version;

        Assert.True(tree.Update(node.Id, node.Title, node.SortOrder).IsSuccess);
        Assert.Equal(before, tree.Version);

        // 真的改了才算改变——否则上面那条断言可能只是因为 Update 从来不涨版本。
        Assert.True(tree.Update(node.Id, MenuTitle.Create("改了").Value, node.SortOrder).IsSuccess);
        Assert.Equal(before + 1, tree.Version);
    }

    [Fact]
    public void MenuTree_FailedOperation_DoesNotBump()
    {
        var tree = NewTree();
        var before = tree.Version;

        Assert.True(tree.Remove(new MenuId(99)).IsFailure);

        Assert.Equal(before, tree.Version);
    }

    // ---------- RefreshToken ----------

    [Fact]
    public void RefreshToken_ConsumeAndRevoke_Bump_ButRepeatsDoNot()
    {
        var token = RefreshToken.Issue(
            new RefreshTokenId(1),
            new UserId(1),
            TokenHash.Create(ValidSecret).Value,
            Now,
            TimeSpan.FromHours(1)).Value;

        Assert.Equal(1, token.Version);

        Assert.True(token.Consume(Now).IsSuccess);
        Assert.Equal(2, token.Version);

        Assert.True(token.Consume(Now).IsFailure);
        Assert.Equal(2, token.Version);

        token.Revoke(Now, "重新登录");
        Assert.Equal(3, token.Version);

        token.Revoke(Now, "再撤一次");
        Assert.Equal(3, token.Version);
    }

    // ---------- ApiResource：不可变，永远停在 1 ----------

    [Fact]
    public void ApiResource_IsImmutable_SoVersionNeverChanges()
    {
        var resource = ApiResource.Create(
            new ApiResourceId(1),
            RoutePattern.Create("/api/x").Value,
            "GET").Value;

        Assert.Equal(1, resource.Version);
    }
}
