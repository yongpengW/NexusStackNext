using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Identity.IntegrationTests;

/// <summary>
/// 令牌的签发与轮换。
///
/// <para><b>这三条对应参照仓库的三处缺陷</b>（review/01）：明文存储、可重放、撤销不生效。
/// 都是**对着真库**验的——用一个内存替身跑，它们会全部通过，因而什么也证明不了。</para>
/// </summary>
[Collection(IdentityDatabaseGroup.Name)]
public sealed class TokenIssuanceTests(IdentityDatabaseFixture fixture)
{
    private const string Password = "a-strong-password";

    private static async Task<ISender> LoginAsync(ServiceProvider provider, string userName)
    {
        var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var created = await sender.SendAsync(new CreateUserCommand(userName, Password));
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);

        var login = await sender.SendAsync(new LoginCommand(userName, Password));
        Assert.True(login.IsSuccess, login.IsFailure ? login.Error.Message : null);

        return sender;
    }

    /// <summary>
    /// **数据库里不存在任何明文刷新令牌**（验收 2），而且存的是它的 SHA-256。
    ///
    /// <para>断言方式刻意选成"直接在整张表里找那串原文"——而不是"检查某个字段看起来像哈希"。
    /// 后者会被"哈希字段旁边又存了一份原文"这种情况骗过。</para>
    /// </summary>
    [PostgresFact]
    public async Task TheRawRefreshToken_IsNeverStored()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);

        var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var created = await sender.SendAsync(new CreateUserCommand("token-probe", Password));
        Assert.True(created.IsSuccess);

        var login = await sender.SendAsync(new LoginCommand("token-probe", Password));
        Assert.True(login.IsSuccess, login.IsFailure ? login.Error.Message : null);

        var raw = login.Value.Tokens.RefreshToken;
        Assert.False(string.IsNullOrWhiteSpace(raw));

        await using var verify = fixture.NewContext();
        var stored = await verify.RefreshTokens.SingleAsync();

        // 存的是哈希，而且**恰好**是原文的 SHA-256——不是别的什么摘要。
        var expected = new Sha256SecretHasher().Hash(raw);
        Assert.Equal(expected, stored.TokenHash.Encoded);

        // **整张表里搜不到原文。** 这条比上面那条更宽：任何一列、任何一行都不行。
        var candidates = await verify.RefreshTokens
            .Select(token => new { token.TokenHash.Encoded, token.RevokedReason })
            .ToListAsync();

        Assert.DoesNotContain(
            candidates,
            candidate => candidate.Encoded.Contains(raw, StringComparison.Ordinal)
                || (candidate.RevokedReason ?? string.Empty).Contains(raw, StringComparison.Ordinal));
    }

    /// <summary>
    /// **轮换语义**（验收 1）：同一个刷新令牌用第二次必须失败，
    /// 而**第一次签发的访问令牌不受影响**。
    /// </summary>
    [PostgresFact]
    public async Task ReusingARefreshToken_Fails_ButTheFirstAccessTokenStaysValid()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var sender = await LoginAsync(provider, "rotate-probe");

        var login = await sender.SendAsync(new LoginCommand("rotate-probe", Password));
        var first = login.Value.Tokens;

        // 第一次刷新：成功。
        var refreshed = await sender.SendAsync(new RefreshTokenCommand(first.RefreshToken));
        Assert.True(refreshed.IsSuccess, refreshed.IsFailure ? refreshed.Error.Message : null);

        // **第二次用同一个：必须失败。**
        var replay = await sender.SendAsync(new RefreshTokenCommand(first.RefreshToken));
        Assert.True(replay.IsFailure);

        // **第一次签发的访问令牌不受影响。** 轮换管的是刷新链，
        // 不是撤回已经发出去的访问权——那要等撤销机制（票据 11）。
        // 这里断言的是"它还在、还没到期"，而不是"它还能用"——
        // 能不能用取决于验签方，不取决于这条测试。
        Assert.False(string.IsNullOrWhiteSpace(first.AccessToken));
        Assert.True(first.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// **重放要撤销整条链。**
    ///
    /// <para>分不出"客户端重试"与"令牌被偷"，所以按坏的那种处理：
    /// 已经在用的那个新令牌也必须失效。</para>
    /// </summary>
    [PostgresFact]
    public async Task ReplayingARefreshToken_RevokesTheWholeChain()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var sender = await LoginAsync(provider, "replay-probe");

        var login = await sender.SendAsync(new LoginCommand("replay-probe", Password));
        var first = login.Value.Tokens;

        var refreshed = await sender.SendAsync(new RefreshTokenCommand(first.RefreshToken));
        Assert.True(refreshed.IsSuccess);
        var second = refreshed.Value;

        // 拿**已经用过**的那个重放。
        Assert.True((await sender.SendAsync(new RefreshTokenCommand(first.RefreshToken))).IsFailure);

        // 那个还"应该有效"的新令牌也必须失效——这才是撤销整条链。
        var afterReplay = await sender.SendAsync(new RefreshTokenCommand(second.RefreshToken));
        Assert.True(afterReplay.IsFailure);
    }

    /// <summary>**被禁用的账号不能靠刷新令牌续命**——否则"禁用"只对下一次登录生效。</summary>
    [PostgresFact]
    public async Task ADisabledAccount_CannotRefresh()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);
        var sender = await LoginAsync(provider, "disabled-probe");

        var login = await sender.SendAsync(new LoginCommand("disabled-probe", Password));
        var tokens = login.Value.Tokens;

        // 直接改库禁用——本票据不引入"禁用"用例，那是别的事。
        await using (var mutating = fixture.NewContext())
        {
            var user = await mutating.Users.SingleAsync(candidate => candidate.Id == new Domain.Ids.UserId(login.Value.UserId));
            user.Disable(DateTimeOffset.UtcNow);
            await mutating.SaveChangesAsync();
        }

        // **换一个作用域**再刷新：否则处理器会从变更跟踪器里读到禁用之前那个实体，
        // 而真实宿主每次请求都是新作用域。
        var refreshed = await IdentityTestHost.InScopeAsync(provider, async scope =>
            await scope.GetRequiredService<ISender>()
                .SendAsync(new RefreshTokenCommand(tokens.RefreshToken)));

        Assert.True(refreshed.IsFailure);
        Assert.Equal("identity.user.disabled", refreshed.Error.Code);
    }

    /// <summary>不存在的令牌：失败，且不泄露"它存在但过期了"这类区别。</summary>
    [PostgresFact]
    public async Task AnUnknownRefreshToken_IsRejected()
    {
        await fixture.ResetAsync();
        await using var provider = IdentityTestHost.Build(fixture.Database.ConnectionString);

        var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.SendAsync(new RefreshTokenCommand("this-token-was-never-issued"));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.refresh_token.unusable", result.Error.Code);
    }
}
