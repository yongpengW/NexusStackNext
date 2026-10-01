using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.ValueObjects;
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
    /// **菜单树在 EF 上真的落库了**：写进去，换一个上下文读回来。
    ///
    /// <para>为什么需要单独一条：菜单树是 `OwnsMany`（`menu_trees` + `menu_nodes` 两张表），
    /// 而票据 67 之前**这个端口根本不存在**——领域聚合写好、有测试，却从来没有被持久化过。
    /// 一个 `OwnsMany` 映射错了（外键、字段访问模式、只读集合）在内存适配器下**完全看不出来**，
    /// 而它会让菜单在真库里少一层节点或整棵树丢失。</para>
    ///
    /// <para>断言刻意落在"节点还在、标题与路径都对"上——那正是 `AddRoot` 之后需要活下来的东西。</para>
    /// </summary>
    [PostgresFact]
    public async Task MenuTree_RoundTripsThroughPostgres()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);

        long treeId;
        long nodeId;

        await using (var scope = provider.CreateAsyncScope())
        {
            var trees = scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>();

            var tree = MenuTree.Create(new MenuTreeId(1));
            var added = tree.AddRoot(new MenuId(1), MenuTitle.Create("后台导航").Value);

            Assert.True(added.IsSuccess, "AddRoot 失败，后面的断言没有对象。");

            await trees.AddAsync(tree);
            await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();

            treeId = tree.Id.Value;
            nodeId = added.Value.Id.Value;
        }

        // **换一个作用域（也就是换一个 DbContext）读回来**——同一个上下文里读到的
        // 可能只是内存里那个对象，证明不了任何落库的事。
        await using (var scope = provider.CreateAsyncScope())
        {
            var trees = scope.ServiceProvider.GetRequiredService<IMenuTreeRepository>();

            var reloaded = await trees.FindAsync();

            Assert.NotNull(reloaded);
            Assert.Equal(treeId, reloaded.Id.Value);

            var node = Assert.Single(reloaded.Nodes);
            Assert.Equal(nodeId, node.Id.Value);
            Assert.Equal("后台导航", node.Title.Value);
            Assert.Null(node.ParentId);
        }
    }

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

    /// <summary>
    /// **连接表的主键是两列**——一张菜单能授给两个角色，一个角色能给两个用户。
    ///
    /// <para><b>它守的是一条真实的静默缺陷。</b>主键曾经只有一列
    /// （<c>PK_user_roles (role_id)</c> / <c>PK_role_menus (menu_id)</c>），
    /// 于是"全库只能存在一条谁有哪个角色"成了数据库层的事实：第二次写入主键冲突。
    /// 而它**到处都看不见**——内存适配器不拦、领域测试不拦（它们各自只分配一次）、
    /// 应用层测试也不拦（同样是内存仓储）。只有真库上的**第二次**才炸。</para>
    ///
    /// <para>所以这条测试的内容就是那第二次：两个用户共用一个角色、两个角色共用一张菜单。</para>
    /// </summary>
    [PostgresFact]
    public async Task JoinTables_AllowMoreThanOneRow()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var first = (await sender.SendAsync(new CreateUserCommand("shared-one", "a-strong-password"))).Value;
        var second = (await sender.SendAsync(new CreateUserCommand("shared-two", "a-strong-password"))).Value;
        var roleA = (await sender.SendAsync(new CreateRoleCommand("shared-a", "角色甲"))).Value;
        var roleB = (await sender.SendAsync(new CreateRoleCommand("shared-b", "角色乙"))).Value;

        // 一个角色给两个用户——单列主键时这里会撞 PK_user_roles。
        Assert.True((await sender.SendAsync(new AssignRoleCommand(first, roleA))).IsSuccess);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(second, roleA))).IsSuccess);

        // 一张菜单授给两个角色——单列主键时这里会撞 PK_role_menus。
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(roleA, 10))).IsSuccess);
        Assert.True((await sender.SendAsync(new GrantMenuToRoleCommand(roleB, 10))).IsSuccess);

        // 直接数连接表：两行才算真的写进去了——这是"两列主键"最直接的证据。
        await using var connection = new Npgsql.NpgsqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            $"select (select count(*) from {IdentityDbContext.SchemaName}.user_roles), "
            + $"(select count(*) from {IdentityDbContext.SchemaName}.role_menus)",
            connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    /// <summary>
    /// **EF 装配那一处把两个拦截器都接上了。**
    ///
    /// <para>它们此前写完了却**没有任何注册点**：审计字段在生产里从不写、领域事件也不进发件箱，
    /// 而"没写"与"没有要写的"从外面看是一样的。接上之后，这件事需要一个守着——
    /// 因为"有没有接上"是**装配**的属性，删掉那一行不会有任何别的测试变红。</para>
    ///
    /// <para>判据落在容器解出来的上下文上：它的选项里必须同时有这两个拦截器。</para>
    /// </summary>
    [PostgresFact]
    public async Task IdentityContext_IsWiredWithBothInterceptors()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var options = context.Database.GetService<IDbContextOptions>();
        var attached = options.FindExtension<CoreOptionsExtension>()?.Interceptors?
            .Select(static interceptor => interceptor.GetType())
            .ToList() ?? [];

        Assert.Contains(typeof(AuditInterceptor), attached);
        Assert.Contains(typeof(DomainEventOutboxInterceptor), attached);

        // 而"映射器缺席"这件事也必须是被决定的：基座注册的是"一律不发布"那个实现，
        // 于是拦截器构造得出来、审计走在正道上（见 InterceptorWiringTests）。
        Assert.IsType<NoIntegrationEventsMapper>(
            scope.ServiceProvider.GetRequiredService<IIntegrationEventMapper>());
    }
}
