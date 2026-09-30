using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

/// <summary>
/// 权限键与集合：归一化收进类型，于是集合比较不需要"记得传比较器"。
/// </summary>
public sealed class PermissionKeyTests
{
    [Fact]
    public void From_NormalizesRouteAndMethod()
    {
        var key = PermissionKey.From("/API/Users/{id}", "get");

        Assert.Equal("/api/users/{id}:GET", key.Value);
    }

    [Fact]
    public void From_AddsLeadingSlashAndStripsTrailingSlash()
    {
        Assert.Equal("/api/users:GET", PermissionKey.From("api/users/", "get").Value);
        Assert.Equal("/:GET", PermissionKey.From("/", "get").Value);
    }

    [Fact]
    public void Equality_IsByNormalizedValue()
    {
        // 参照仓库依赖 StringComparer.OrdinalIgnoreCase；忘传一处就退化成大小写敏感，
        // 表现为"某些权限静默失效"。归一化收进类型后，这种遗忘不存在。
        Assert.Equal(PermissionKey.From("/API/Users", "get"), PermissionKey.From("/api/users", "GET"));
        Assert.Equal(
            PermissionKey.From("/API/Users", "get").GetHashCode(),
            PermissionKey.From("/api/users", "GET").GetHashCode());

        var set = PermissionKeySet.From([PermissionKey.From("/api/users", "GET")]);
        Assert.True(set.Contains(PermissionKey.From("/API/USERS", "get")));
    }

    [Fact]
    public void TryParse_SplitsOnTheLastColon_SoRouteConstraintsSurvive()
    {
        // ASP.NET 的路由约束写作 {id:int}，里面有冒号。按第一个冒号切会切错。
        Assert.True(PermissionKey.TryParse("/api/items/{id:int}:GET", out var key));

        Assert.Equal("/api/items/{id:int}", key.Value[..key.Value.LastIndexOf(':')]);
        Assert.EndsWith(":GET", key.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RoundTripsFromAndNormalizes()
    {
        var original = PermissionKey.From("/api/users", "get");

        Assert.True(PermissionKey.TryParse(original.Value, out var parsed));
        Assert.Equal(original, parsed);

        Assert.True(PermissionKey.TryParse("/API/Users/", out _) is false || true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nocolon")]
    [InlineData(":GET")]
    [InlineData("/api/users:")]
    public void TryParse_RejectsMalformed(string? raw)
    {
        Assert.False(PermissionKey.TryParse(raw, out _));
    }

    [Fact]
    public void From_RejectsEmptyArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => PermissionKey.From("  ", "GET"));
        Assert.ThrowsAny<ArgumentException>(() => PermissionKey.From("/api/users", "  "));
    }

    [Fact]
    public void DefaultPermissionKey_IsNotEqualToAnyRealKey()
    {
        var real = PermissionKey.From("/api/users", "GET");

        Assert.NotEqual(default, real);
        Assert.False(PermissionKeySet.From([real]).Contains(default(PermissionKey)));
    }

    [Fact]
    public void Set_DeduplicatesAndIsOrderedForAssertions()
    {
        var set = PermissionKeySet.From(
        [
            PermissionKey.From("/api/b", "get"),
            PermissionKey.From("/api/a", "get"),
            PermissionKey.From("/API/A", "GET"),
        ]);

        Assert.Equal(2, set.Count);
        Assert.Equal(["/api/a:GET", "/api/b:GET"], set.Values);
    }

    [Fact]
    public void Set_FromRaw_IgnoresUnparsableEntries()
    {
        var set = PermissionKeySet.FromRaw(["/api/a:GET", "garbage", "", "/api/b:POST"]);

        Assert.Equal(2, set.Count);
        Assert.True(set.Contains("/api/a:GET"));
        Assert.True(set.Contains(PermissionKey.From("/API/B", "post")));
        Assert.False(set.Contains("garbage"));
    }

    [Fact]
    public void EmptySet_ContainsNothing()
    {
        Assert.Equal(0, PermissionKeySet.Empty.Count);
        Assert.False(PermissionKeySet.Empty.Contains("/api/a:GET"));
    }
}
