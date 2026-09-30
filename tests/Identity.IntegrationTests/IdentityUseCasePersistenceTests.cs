using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// 用例经**分发器**执行时的落库行为。
///
/// <para><b>它守的是一条真实存在过的缺陷。</b>端点此前是内联的，而且**不调用
/// <c>SaveChanges</c></b>：内存存储下看不出问题（内存版保存的是聚合实例本身，
/// 改动自动可见），但换成 EF 之后，变更类端点会**静默地不落库**而接口照返回 204。
/// 平台宿主当时注册的正是内存版——所以它不是活的缺陷，而是一颗上了膛的枪。</para>
///
/// <para>这组测试**刻意用 EF 存储**：用内存版它们会全部通过，因而什么也证明不了。
/// 这正是"验证方式要贴近真实运行路径"那条教训的又一次应用。</para>
/// </summary>
[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityUseCasePersistenceTests(IdentityDatabaseFixture fixture)
{
    /// <summary>
    /// **经分发器创建的用户确实落库了**——用另一个上下文在进程外验证。
    ///
    /// <para>这正是内联端点做不到的事：它们改完内存就返回了。</para>
    /// </summary>
    [PostgresFact]
    public async Task CommandThroughTheDispatcher_IsPersisted()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var created = await sender.SendAsync(new CreateUserCommand("leo", "a-strong-password"));
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);

        var roleCreated = await sender.SendAsync(new CreateRoleCommand("admin", "管理员"));
        Assert.True(roleCreated.IsSuccess, roleCreated.IsFailure ? roleCreated.Error.Message : null);

        // **换一个上下文读**：它证明的是"真的进了数据库"，
        // 而不是"内存里那个对象被改了"。
        await using var verify = fixture.NewContext();

        var user = await new EfUserRepository(verify).FindAsync(new UserId(created.Value));
        Assert.NotNull(user);
        Assert.Equal("leo", user.UserName.Value);

        var role = await new EfRoleRepository(verify).FindByCodeAsync(
            NexusStackNext.Identity.Domain.Roles.RoleCode.Create("admin").Value);
        Assert.NotNull(role);
        Assert.Equal(roleCreated.Value, role.Id.Value);
    }

    /// <summary>
    /// **命令失败时不落任何东西**——分发器只在成功后保存。
    ///
    /// <para>反向的那一半：只验"成功会保存"是不够的，一个"无论成败都保存"的实现同样能通过。</para>
    /// </summary>
    [PostgresFact]
    public async Task FailedCommand_LeavesNothingBehind()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // 用户名太短——领域会拒绝，处理器返回失败。
        var rejected = await sender.SendAsync(new CreateUserCommand("x", "a-strong-password"));
        Assert.True(rejected.IsFailure);

        await using var verify = fixture.NewContext();
        Assert.Empty(await verify.Users.ToListAsync());
    }

    /// <summary>
    /// **授权变更会让权限缓存失效**——而且是在用例内部失效的，不依赖调用方记得。
    ///
    /// <para>把失效留给调用方，就会出现"某条路径忘了失效"，而那正是参照仓库
    /// 10 小时窗口的成因（review/04）。</para>
    /// </summary>
    [PostgresFact]
    public async Task GrantingAMenu_InvalidatesThePermissionCache()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var userId = (await sender.SendAsync(new CreateUserCommand("leo", "a-strong-password"))).Value;
        var roleId = (await sender.SendAsync(new CreateRoleCommand("admin", "管理员"))).Value;

        // **先分配角色**——没有这一步，后面授予的菜单与资源都与这个用户无关。
        // （第一版测试漏了它，于是断言"有一条权限"失败在了一个空集合上。）
        Assert.True((await sender.SendAsync(new AssignRoleCommand(userId, roleId))).IsSuccess);

        // 一开始没有任何权限：角色有了，但它还没有任何菜单。
        var before = await sender.QueryAsync(new GetUserPermissionsQuery(userId));
        Assert.True(before.IsSuccess);
        Assert.Empty(before.Value);

        // 授予一个菜单、并把一条 API 资源挂上去——这条链路走完，权限才真的存在。
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(roleId, 10))).IsSuccess);
        Assert.True((await sender.SendAsync(
            new CreateApiResourceCommand("/api/identity/users", "GET", 10))).IsSuccess);

        // **紧接着**查询——没有等任何 TTL，也没有手工失效。
        var after = await sender.QueryAsync(new GetUserPermissionsQuery(userId));

        Assert.True(after.IsSuccess);
        Assert.Equal("/api/identity/users:GET", Assert.Single(after.Value));
    }
}
