namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary>
/// <see cref="Entity{TId}"/> 的行为。重点在于：ID 由调用方提供，构造不碰任何全局状态。
/// </summary>
public sealed class EntityTests
{
    [Fact]
    public void Construct_WithExplicitId_KeepsThatId()
    {
        var id = new UserId(42);

        var user = User.Register(id, UserName.Create("leo").Value);

        Assert.Equal(id, user.Id);
    }

    [Fact]
    public void Construct_WithDefaultId_Throws()
    {
        // 参照仓库的反例：构造函数里生成 ID，于是 ID 永远不可能"缺失"，测试也无法控制它。
        Assert.Throws<ArgumentException>(() => User.Register(null!, UserName.Create("leo").Value));
    }

    [Fact]
    public void Equality_SameIdAndSameType_AreEqual()
    {
        var a = User.Register(new UserId(7), UserName.Create("leo").Value);
        var b = User.Register(new UserId(7), UserName.Create("leo").Value);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentIds_AreNotEqual()
    {
        var a = User.Register(new UserId(7), UserName.Create("leo").Value);
        var b = User.Register(new UserId(8), UserName.Create("leo").Value);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_IncludesId()
    {
        var user = User.Register(new UserId(99), UserName.Create("leo").Value);

        Assert.Contains("99", user.ToString(), StringComparison.Ordinal);
    }
}
