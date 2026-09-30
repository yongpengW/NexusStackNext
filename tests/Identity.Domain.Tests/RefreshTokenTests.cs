using System.Reflection;
using NexusStackNext.Identity.Domain.Events;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>
/// 刷新令牌。核心：只存哈希、一次性使用、明文在结构上不存在。
/// </summary>
public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static TokenHash Hash(string seed = "c") => TokenHash.Create(new string(seed[0], 64)).Value;

    private static RefreshToken NewToken() =>
        RefreshToken.Issue(new RefreshTokenId(1), new UserId(42), Hash(), Now, Lifetime).Value;

    [Fact]
    public void Issue_RaisesTokenIssued_AndComputesExpiry()
    {
        var token = NewToken();

        var issued = Assert.IsType<RefreshTokenIssued>(Assert.Single(token.DomainEvents));
        Assert.Equal(1, issued.TokenId.Value);
        Assert.Equal(42, issued.UserId.Value);
        Assert.Equal(Now + Lifetime, token.ExpiresAt);
        Assert.True(token.IsUsable(Now));
    }

    [Fact]
    public void Issue_NonPositiveLifetime_IsRejected()
    {
        var result = RefreshToken.Issue(new RefreshTokenId(1), new UserId(42), Hash(), Now, TimeSpan.Zero);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.refresh_token.invalid_lifetime", result.Error.Code);
    }

    [Fact]
    public void Consume_Twice_SecondIsRejected()
    {
        // 参照仓库的令牌明文入库且"校验后再作废"跨三个 await 无并发保护，同一条能被用两次。
        var token = NewToken();

        Assert.True(token.Consume(Now).IsSuccess);

        var replay = token.Consume(Now + TimeSpan.FromSeconds(1));
        Assert.True(replay.IsFailure);
        Assert.Equal("identity.refresh_token.unusable", replay.Error.Code);
        Assert.Equal(Now, token.ConsumedAt);
    }

    [Fact]
    public void Consume_AfterRevoke_IsRejected()
    {
        var token = NewToken();
        token.Revoke(Now, "用户登出");

        var result = token.Consume(Now);

        Assert.True(result.IsFailure);
        Assert.Contains("已撤销", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consume_AtExactExpiry_IsRejected()
    {
        // 边界：到期的那一刻即失效。
        var token = NewToken();

        Assert.True(token.Consume(Now + Lifetime - TimeSpan.FromTicks(1)).IsSuccess);

        var expired = RefreshToken.Issue(new RefreshTokenId(2), new UserId(42), Hash(), Now, Lifetime).Value;
        Assert.True(expired.Consume(Now + Lifetime).IsFailure);
    }

    [Fact]
    public void Revoke_IsIdempotent_AndRaisesEventOnlyOnce()
    {
        var token = NewToken();
        token.ClearDomainEvents();

        Assert.True(token.Revoke(Now, "登出").IsSuccess);
        Assert.IsType<RefreshTokenRevoked>(Assert.Single(token.DomainEvents));

        token.ClearDomainEvents();
        Assert.True(token.Revoke(Now, "再次登出").IsSuccess);
        Assert.Empty(token.DomainEvents);
        Assert.Equal("登出", token.RevokedReason);
    }

    [Fact]
    public void Revoke_RequiresAReason()
    {
        var token = NewToken();

        Assert.ThrowsAny<ArgumentException>(() => token.Revoke(Now, "  "));
    }

    [Fact]
    public void RefreshToken_ExposesNoPlaintextTokenMember()
    {
        // "只存哈希"最可靠的保证不是纪律，而是**根本没有那个字段**。
        var members = typeof(RefreshToken)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .ToList();

        // **判据要落在"名字里有没有那个词"上，而不是精确匹配。**
        //
        // 原来写的是 `Assert.DoesNotContain("Token", members)`：那是**元素精确匹配**——
        // 一个叫 `PlainTokenValue` 的属性照样通过，而它正是这条测试要拦的东西。
        // 现在反过来问："名字里带 Token 的成员，**恰好只有** TokenHash 吗？"——
        // 多出任何一个都会红，无论它叫什么。
        Assert.Equal(["TokenHash"], members.Where(m => m.Contains("Token", StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain(members, m =>
            m.Contains("Plain", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Raw", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Secret", StringComparison.OrdinalIgnoreCase));

        // 这一条**用精确匹配是对的**：它问的是"有没有一个成员就叫 `Value`"——
        // 那是"直接把原文暴露出去"最省事的写法。
        Assert.DoesNotContain("Value", members);
    }

    [Fact]
    public void User_ExposesNoPlaintextPasswordMember()
    {
        var members = typeof(User)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .ToList();

        // 同上：判据是"名字里带 Password 的成员恰好只有 PasswordHash"。
        Assert.Equal(["PasswordHash"], members.Where(m => m.Contains("Password", StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain(members, m => m.Contains("Plain", StringComparison.OrdinalIgnoreCase));
    }
}
