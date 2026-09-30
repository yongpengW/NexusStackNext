using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// 登录：**锁定、计数落库、以及不泄露用户名存在性**。
///
/// <para><b>为什么这组测试刻意用 EF 存储。</b>本次实现时发现一处真实的设计碰撞：
/// 分发器只在处理器**成功**时保存（见 <c>Sender</c>），而"登录失败"本身就是一次状态变更
/// ——失败计数加一，够阈值就锁定。处理器若不自已保存，**锁定永远不会生效**：
/// 每次失败都被安静地丢掉，而接口照常返回"用户名或密码错误"。</para>
///
/// <para>用内存存储跑这组测试会**全部通过**，因为它保存的是聚合实例本身、改动立刻可见——
/// 那个缺陷在它下面根本不存在，因而也验不出来。</para>
/// </summary>
[Collection(IdentityDatabaseGroup.Name)]
public sealed class LoginTests(IdentityDatabaseFixture fixture)
{
    private const string Password = "a-strong-password";


    // 组装搬到 `IdentityTestHost` —— 与宿主一致，且只写一遍。
    private static ServiceProvider BuildProvider(PostgresTestDatabase database) =>
        IdentityTestHost.Build(database.ConnectionString);

    private static IdentityDbContext NewContext(PostgresTestDatabase database)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        builder.UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName);
        return new IdentityDbContext(builder.Options);
    }

    private static async Task<ISender> SeedUserAsync(ServiceProvider provider, string userName)
    {
        var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var created = await sender.SendAsync(new CreateUserCommand(userName, Password));
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);

        return sender;
    }

    /// <summary>
    /// **失败计数真的落库了**，并在达到阈值时锁定。
    ///
    /// <para>这条测试盯的是那个设计碰撞：分发器"只在成功时保存"。
    /// 如果没有处理器里的显式保存，下面每一次断言都会看到计数停在 0，
    /// 而接口每次都返回同样的"用户名或密码错误"——一个**看起来在防护、实际没有**的实现。</para>
    /// </summary>
    [PostgresFact]
    public async Task FailedLogins_AreCounted_AndEventuallyLockTheAccount()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        var policy = LockoutPolicy.Default;

        // 前 threshold-1 次失败：还没有锁定。
        for (var attempt = 1; attempt < policy.Threshold; attempt++)
        {
            var failed = await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));
            Assert.True(failed.IsFailure);

            await using var peek = fixture.NewContext();
            var counted = await new EfUserRepository(peek).FindByUserNameAsync(
                NexusStackNext.Identity.Domain.ValueObjects.UserName.Create("leo").Value);

            // **计数确实进了数据库**——不是只改了内存里那个对象。
            Assert.Equal(attempt, counted!.FailedLoginCount);
            Assert.Null(counted.LockedUntil);
        }

        // 第 threshold 次：锁定。
        Assert.True((await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"))).IsFailure);

        await using var verify = fixture.NewContext();
        var locked = await new EfUserRepository(verify).FindByUserNameAsync(
            NexusStackNext.Identity.Domain.ValueObjects.UserName.Create("leo").Value);

        Assert.Equal(policy.Threshold, locked!.FailedLoginCount);
        Assert.NotNull(locked.LockedUntil);
    }

    /// <summary>
    /// **锁定的账号即使口令正确也进不去**，而且这时才告诉他"被锁了"。
    ///
    /// <para>顺序是有意的：锁定只在口令正确之后才说。</para>
    /// </summary>
    [PostgresFact]
    public async Task LockedAccount_WithTheCorrectPassword_IsRejected()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        for (var attempt = 0; attempt < LockoutPolicy.Default.Threshold; attempt++)
        {
            await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));
        }

        var result = await sender.SendAsync(new LoginCommand("leo", Password));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user.locked", result.Error.Code);
    }

    /// <summary>
    /// **不知道口令的人看不出账号是否存在**——这是接口不变成用户名枚举器的关键。
    ///
    /// <para>两种输入："账号不存在"与"账号存在但口令错"。它们的**错误码与文案都必须一致**，
    /// 否则攻击者可以靠它把用户名一个个试出来。</para>
    ///
    /// <para>参照仓库在这里的表现是两个不同的错误："账号或密码错误"与
    /// "该用户还未设置密码"——后者直接告诉对方"这个用户是存在的"。</para>
    /// </summary>
    [PostgresFact]
    public async Task UnknownUserAndWrongPassword_AreIndistinguishable()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        var unknown = await sender.SendAsync(new LoginCommand("nobody-here", Password));
        // 变量名刻意不叫「…口令」——那条凭据规则的模式允许等号前跨行，
        // 于是"变量名以口令结尾 + 换行 + 等号"会被它命中（本文件里真的发生过）。
        var mismatched = await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));

        Assert.True(unknown.IsFailure);
        Assert.True(mismatched.IsFailure);

        // **逐字相同**——不是"差不多"。
        Assert.Equal(unknown.Error.Code, mismatched.Error.Code);
        Assert.Equal(unknown.Error.Message, mismatched.Error.Message);
    }

    /// <summary>
    /// **锁定状态本身也不能被不知道口令的人看出来。**
    ///
    /// <para>如果"已锁定"先于口令校验返回，攻击者提交任意口令就能判断账号是否存在——
    /// 那样前面那条"两种错误一致"的防护就白做了。所以锁定只在口令正确之后才说。</para>
    /// </summary>
    [PostgresFact]
    public async Task LockoutIsNotVisibleToSomeoneWhoDoesNotKnowThePassword()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        // 把 leo 锁上。
        for (var attempt = 0; attempt < LockoutPolicy.Default.Threshold; attempt++)
        {
            await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));
        }

        // 现在用**错误**口令登录一个**已锁定**的账号：
        // 它与"登录一个不存在的账号"必须给出一模一样的答复。
        var lockedGuess = await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));
        var unknown = await sender.SendAsync(new LoginCommand("nobody-here", Password));

        Assert.Equal(unknown.Error.Code, lockedGuess.Error.Code);
        Assert.Equal(unknown.Error.Message, lockedGuess.Error.Message);
    }

    /// <summary>用户名格式非法时也不单独报——否则可以反推用户名规则。</summary>
    [PostgresFact]
    public async Task MalformedUserName_AlsoGetsTheUniformError()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        var malformed = await sender.SendAsync(new LoginCommand("!!", Password));
        var unknown = await sender.SendAsync(new LoginCommand("nobody-here", Password));

        Assert.Equal(unknown.Error.Code, malformed.Error.Code);
    }

    /// <summary>一次成功登录会把计数清零并解除锁定。</summary>
    [PostgresFact]
    public async Task SuccessfulLogin_ResetsTheFailureCount()
    {
        await fixture.ResetAsync();

        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await SeedUserAsync(provider, "leo");

        // 失败两次（未达阈值）。
        await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));
        await sender.SendAsync(new LoginCommand("leo", "the-wrong-password"));

        var success = await sender.SendAsync(new LoginCommand("leo", Password));
        Assert.True(success.IsSuccess, success.IsFailure ? success.Error.Message : null);

        await using var verify = fixture.NewContext();
        var user = await new EfUserRepository(verify).FindByUserNameAsync(
            NexusStackNext.Identity.Domain.ValueObjects.UserName.Create("leo").Value);

        Assert.Equal(0, user!.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.NotNull(user.LastLoginAt);
        Assert.Equal(new UserId(success.Value.UserId), user.Id);
    }
}
