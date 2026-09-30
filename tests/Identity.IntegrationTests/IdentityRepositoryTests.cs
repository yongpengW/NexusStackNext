using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// EF Core 仓储，对着真实 PostgreSQL 验证。
///
/// <para><b>它们与内存版是同一个端口的两个适配器</b>，所以这组测试要盯的是
/// "换了适配器，行为会不会变"——尤其是查询翻译：内存版用 LINQ-to-Objects，
/// EF 版用 LINQ-to-Entities，而后者会拒译它不认识的表达式。</para>
/// </summary>
[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityRepositoryTests(IdentityDatabaseFixture fixture)
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);


    private static IdentityDbContext NewContext(PostgresTestDatabase database)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        builder.UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName);
        return new IdentityDbContext(builder.Options);
    }

    private static User NewUser(long id, string name, string hashSeed)
        => User.Register(
            new UserId(id),
            UserName.Create(name).Value,
            PasswordHash.Create(new string(hashSeed[0], 64)).Value,
            RegisteredAt);

    /// <summary>添加之后按标识读回来，状态一致。</summary>
    [PostgresFact]
    public async Task UserRepository_RoundTripsTheAggregate()
    {
        await fixture.ResetAsync();

        await using var context = fixture.NewContext();
        var repository = new EfUserRepository(context);

        await repository.AddAsync(NewUser(1, "leo", "a"));

        await using var reader = fixture.NewContext();
        var loaded = await new EfUserRepository(reader).FindAsync(new UserId(1));

        Assert.NotNull(loaded);
        Assert.Equal("leo", loaded.UserName.Value);
        Assert.True(loaded.IsEnabled);
        Assert.False(loaded.IsBuiltIn);

        // 找不到的标识返回 null，而不是抛异常。
        Assert.Null(await new EfUserRepository(reader).FindAsync(new UserId(999)));
    }

    /// <summary>
    /// <c>UserNameExistsAsync</c> 把一个**值对象**的比较交给了数据库。
    ///
    /// <para>这条测试真正在验的是**查询翻译**：内存版用 LINQ-to-Objects，
    /// 怎么写都能跑；EF 版必须把 <c>user.UserName == userName</c> 翻译成对
    /// 转换后列的等值比较。翻译不了会抛 <c>InvalidOperationException</c>，
    /// 而那正是"换适配器行为就变了"的典型形态。</para>
    /// </summary>
    [PostgresFact]
    public async Task UserNameExistsAsync_IsTranslatedToSql()
    {
        await fixture.ResetAsync();

        await using var context = fixture.NewContext();
        var repository = new EfUserRepository(context);

        Assert.False(await repository.UserNameExistsAsync(UserName.Create("leo").Value));

        await repository.AddAsync(NewUser(1, "leo", "a"));

        Assert.True(await repository.UserNameExistsAsync(UserName.Create("leo").Value));
        Assert.False(await repository.UserNameExistsAsync(UserName.Create("someone-else").Value));
    }

    /// <summary>
    /// **读取 → 改 → 保存**：这条路径必须经 <c>IUnitOfWork.SaveChangesAsync</c> 才落库。
    ///
    /// <para>它正是 EF 版与内存版语义不同的地方（内存版保存的是实例本身，改动自动可见）。
    /// 所以这里既验"保存之后确实落库"，也验"不保存就不落库"——
    /// 只验前者的话，一个"每次读取都自动保存"的实现同样能通过。</para>
    /// </summary>
    [PostgresFact]
    public async Task LoadModifySave_RequiresSaveChanges()
    {
        await fixture.ResetAsync();

        await using (var seed = fixture.NewContext())
        {
            await new EfUserRepository(seed).AddAsync(NewUser(1, "leo", "a"));
        }

        await using (var context = fixture.NewContext())
        {
            var unitOfWork = new EfUnitOfWork<IdentityDbContext>(context);
            var user = await new EfUserRepository(context).FindAsync(new UserId(1));

            Assert.NotNull(user);

            user.ChangePassword(PasswordHash.Create(new string('b', 64)).Value, RegisteredAt.AddHours(1));

            // **还没保存**——换一个上下文看，库里的还是旧值。
            await using (var peek = fixture.NewContext())
            {
                var beforeSave = await new EfUserRepository(peek).FindAsync(new UserId(1));
                Assert.Equal(PasswordHash.Create(new string('a', 64)).Value, beforeSave!.PasswordHash);
            }

            await unitOfWork.SaveChangesAsync();
        }

        await using (var verify = fixture.NewContext())
        {
            var afterSave = await new EfUserRepository(verify).FindAsync(new UserId(1));
            Assert.Equal(PasswordHash.Create(new string('b', 64)).Value, afterSave!.PasswordHash);
        }
    }

    /// <summary>批量按标识查找；找不到的标识被忽略，结果按标识有序。</summary>
    [PostgresFact]
    public async Task RoleRepository_FindMany_IgnoresMissingIds_AndOrdersById()
    {
        await fixture.ResetAsync();

        await using var context = fixture.NewContext();
        var repository = new EfRoleRepository(context);

        await repository.AddAsync(Role.Create(new RoleId(2), RoleCode.Create("editor").Value, RoleName.Create("编辑").Value));
        await repository.AddAsync(Role.Create(new RoleId(1), RoleCode.Create("admin").Value, RoleName.Create("管理员").Value));

        var found = await repository.FindManyAsync([new RoleId(1), new RoleId(2), new RoleId(999)]);

        Assert.Equal(2, found.Count);
        Assert.Equal("admin", found[0].Code.Value);
        Assert.Equal("editor", found[1].Code.Value);

        // 按编码查找。
        var byCode = await repository.FindByCodeAsync(RoleCode.Create("editor").Value);
        Assert.NotNull(byCode);
        Assert.Equal(new RoleId(2), byCode.Id);

        Assert.Null(await repository.FindByCodeAsync(RoleCode.Create("nobody").Value));
    }

    /// <summary>按菜单批量查 API 资源；只返回挂在该菜单下的。</summary>
    [PostgresFact]
    public async Task ApiResourceRepository_FindByMenuIds()
    {
        await fixture.ResetAsync();

        await using var context = fixture.NewContext();
        var repository = new EfApiResourceRepository(context);

        await repository.AddAsync(ApiResource.Create(
            new ApiResourceId(1), RoutePattern.Create("/api/identity/users").Value, "GET", new MenuId(10)).Value);
        await repository.AddAsync(ApiResource.Create(
            new ApiResourceId(2), RoutePattern.Create("/api/identity/roles").Value, "GET", new MenuId(20)).Value);
        await repository.AddAsync(ApiResource.Create(
            new ApiResourceId(3), RoutePattern.Create("/api/identity").Value, "GET", null).Value);

        var found = await repository.FindByMenuIdsAsync(new HashSet<MenuId> { new(10) });

        Assert.Single(found);
        Assert.Equal(new ApiResourceId(1), found[0].Id);

        // 空集合：不查库，直接空结果。
        Assert.Empty(await repository.FindByMenuIdsAsync(new HashSet<MenuId>()));
    }

    /// <summary>
    /// **唯一索引是最后的防线**：即使绕过了 <c>UserNameExistsAsync</c>，数据库仍然挡住重复。
    ///
    /// <para>两处一起才成立：仓储负责"常见路径走快"，索引负责"任何路径都不放行"。
    /// 只靠前者有竞态，只靠后者则每次都要靠异常来表达一个可预期的情况。</para>
    /// </summary>
    [PostgresFact]
    public async Task UniqueIndex_IsTheLastLineOfDefence()
    {
        await fixture.ResetAsync();

        await using (var first = fixture.NewContext())
        {
            await new EfUserRepository(first).AddAsync(NewUser(1, "leo", "a"));
        }

        await using var second = fixture.NewContext();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => new EfUserRepository(second).AddAsync(NewUser(2, "leo", "b")));

        Assert.Equal("23505", Assert.IsType<Npgsql.PostgresException>(exception.InnerException).SqlState);
    }
}
