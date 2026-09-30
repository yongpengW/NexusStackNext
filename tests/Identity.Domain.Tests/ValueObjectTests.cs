using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Domain.Tests;

/// <summary>值对象：边界、规范化与相等性。</summary>
public sealed class ValueObjectTests
{
    // ---------- UserName ----------

    [Theory]
    [InlineData("leo", "leo")]
    [InlineData("  leo  ", "leo")]
    [InlineData("abc", "abc")]
    public void UserName_NormalizesValidInput(string input, string expected)
    {
        Assert.Equal(expected, UserName.Create(input).Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UserName_RejectsEmpty(string? input)
    {
        Assert.True(UserName.Create(input).IsFailure);
    }

    [Fact]
    public void UserName_EnforcesLengthBoundaries()
    {
        // 下边界：2 太短，3 刚好。
        Assert.True(UserName.Create(new string('a', UserName.MinLength - 1)).IsFailure);
        Assert.True(UserName.Create(new string('a', UserName.MinLength)).IsSuccess);

        // 上边界：32 刚好，33 太长。
        Assert.True(UserName.Create(new string('a', UserName.MaxLength)).IsSuccess);
        Assert.True(UserName.Create(new string('a', UserName.MaxLength + 1)).IsFailure);
    }

    [Fact]
    public void UserName_RejectsInnerWhitespace()
    {
        Assert.Equal("identity.user_name.whitespace", UserName.Create("le o").Error.Code);
    }

    [Fact]
    public void UserName_EqualityIsByValue()
    {
        Assert.Equal(UserName.Create("leo").Value, UserName.Create(" leo ").Value);
        Assert.NotEqual(UserName.Create("leo").Value, UserName.Create("neo").Value);
    }

    // ---------- EmailAddress ----------

    [Theory]
    [InlineData("Leo@Example.COM", "leo@example.com")]
    [InlineData(" a@b.co ", "a@b.co")]
    public void EmailAddress_NormalizesToLowerCase(string input, string expected)
    {
        Assert.Equal(expected, EmailAddress.Create(input).Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@at.com")]
    [InlineData("no-dot@domain")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("has space@example.com")]
    public void EmailAddress_RejectsMalformed(string? input)
    {
        Assert.True(EmailAddress.Create(input).IsFailure);
    }

    // ---------- PhoneNumber ----------

    [Theory]
    [InlineData("13800138000")]
    [InlineData("+8613800138000")]
    [InlineData(" 123456 ")]
    public void PhoneNumber_AcceptsDigitsWithOptionalPlus(string input)
    {
        Assert.True(PhoneNumber.Create(input).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("123456789012345678901")]
    [InlineData("138-0013-8000")]
    [InlineData("++8613800138000")]
    public void PhoneNumber_RejectsMalformed(string? input)
    {
        Assert.True(PhoneNumber.Create(input).IsFailure);
    }

    [Fact]
    public void PhoneNumber_DoesNotHardCodeOneCountry()
    {
        // 参照仓库写死了中国大陆规则，换个地区就废。
        Assert.True(PhoneNumber.Create("+14155552671").IsSuccess);
    }

    // ---------- SecretHash ----------

    [Fact]
    public void PasswordHash_RejectsShortOrWhitespaceValues()
    {
        Assert.True(PasswordHash.Create(new string('a', SecretHash.MinLength - 1)).IsFailure);
        Assert.True(PasswordHash.Create(new string('a', SecretHash.MinLength)).IsSuccess);
        Assert.True(PasswordHash.Create("abc def ghijklmnopqrstuvwxyz0123456789").IsFailure);
        Assert.True(PasswordHash.Create("   ").IsFailure);
    }

    [Fact]
    public void PasswordHash_And_TokenHash_AreNotInterchangeable()
    {
        var encoded = new string('a', 64);

        Assert.NotEqual<object>(PasswordHash.Create(encoded).Value, TokenHash.Create(encoded).Value);
    }

    // ---------- RoutePattern ----------

    [Fact]
    public void RoutePattern_NormalizesCaseAndTrailingSlash()
    {
        Assert.Equal("/api/users/{id}", RoutePattern.Create("/API/Users/{id}/").Value.Value);
        Assert.Equal("/", RoutePattern.Create("/").Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api/users")]
    [InlineData("/api /users")]
    public void RoutePattern_RejectsMalformed(string? input)
    {
        Assert.True(RoutePattern.Create(input).IsFailure);
    }

    [Fact]
    public void RoutePattern_BuildsThePermissionKeyUsedAtAuthorizationTime()
    {
        // 格式沿用参照仓库已验证的预计算设计（UserContextCacheService.cs:96-119）：
        // 鉴权时是一次哈希集合查找，而不是每请求查库。
        var pattern = RoutePattern.Create("/api/Users/{id}").Value;

        var key = pattern.ToPermissionKey("get");

        Assert.Equal("/api/users/{id}:GET", key.Value);
    }

    [Fact]
    public void RoutePattern_RejectsEmptyHttpMethod()
    {
        var pattern = RoutePattern.Create("/api/users").Value;

        Assert.ThrowsAny<ArgumentException>(() => pattern.ToPermissionKey(" "));
    }

    // ---------- MenuPath ----------

    [Fact]
    public void MenuPath_AppendBuildsSequence()
    {
        var path = MenuPath.Root.Append(new MenuId(1)).Append(new MenuId(5)).Append(new MenuId(12));

        Assert.Equal("/1/5/12/", path.ToSequenceString());
        Assert.Equal(3, path.Depth);
        Assert.Equal([1L, 5L, 12L], path.Segments);
        Assert.Equal("/", MenuPath.Root.ToSequenceString());
    }

    [Fact]
    public void MenuPath_IsAncestorOf_IsStrict()
    {
        var parent = MenuPath.From(1, 5);
        var child = MenuPath.From(1, 5, 12);
        var sibling = MenuPath.From(1, 6);

        Assert.True(parent.IsAncestorOf(child));
        Assert.False(parent.IsAncestorOf(parent));
        Assert.False(parent.IsAncestorOf(sibling));
        Assert.False(child.IsAncestorOf(parent));
    }

    [Fact]
    public void MenuPath_DoesNotConfuseNumericPrefixes()
    {
        // 段比较而不是子串比较：路径 /1/ 不是 /12/ 的祖先。
        var one = MenuPath.From(1);
        var twelve = MenuPath.From(12);
        var twelveChild = MenuPath.From(12, 5);

        Assert.False(one.IsAncestorOf(twelve));
        Assert.False(one.IsAncestorOf(twelveChild));
        Assert.True(twelve.IsAncestorOf(twelveChild));
    }

    [Fact]
    public void MenuPath_RebaseRewritesOnlyTheMatchingPrefix()
    {
        var oldRoot = MenuPath.From(1, 10);
        var newRoot = MenuPath.From(2, 10);

        Assert.Equal("/2/10/100/1000/", MenuPath.From(1, 10, 100, 1000).Rebase(oldRoot, newRoot).ToSequenceString());

        // 不是以该前缀开头：原样返回。
        var unrelated = MenuPath.From(9, 9, 9);
        Assert.Equal(unrelated, unrelated.Rebase(oldRoot, newRoot));

        // 与前缀相等：原样返回（自身不由 Rebase 改写）。
        Assert.Equal(oldRoot, oldRoot.Rebase(oldRoot, newRoot));
    }

    [Fact]
    public void MenuPath_EqualityIsBySegments()
    {
        Assert.Equal(MenuPath.From(1, 2, 3), MenuPath.From(1, 2, 3));
        Assert.NotEqual(MenuPath.From(1, 2, 3), MenuPath.From(1, 2));
    }
}
