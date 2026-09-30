namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary>强类型 ID 与值对象的相等性语义。</summary>
public sealed class ValueObjectTests
{
    [Fact]
    public void StronglyTypedId_SameValue_AreEqual()
    {
        Assert.Equal(new UserId(5), new UserId(5));
        Assert.Equal(new UserId(5).GetHashCode(), new UserId(5).GetHashCode());
    }

    [Fact]
    public void StronglyTypedId_DifferentValue_AreNotEqual()
    {
        Assert.NotEqual(new UserId(5), new UserId(6));
    }

    [Fact]
    public void StronglyTypedId_KeepsUnderlyingValue()
    {
        Assert.Equal(5L, new UserId(5).Value);
        Assert.Equal("5", new UserId(5).ToString());
    }

    [Fact]
    public void StronglyTypedId_RejectsDefaultValue()
    {
        // 0 与 Guid.Empty 不是合法 ID；让它在构造处就失败，而不是等到写库时与种子数据撞车。
        Assert.Throws<ArgumentException>(() => new UserId(0));
    }

    [Fact]
    public void ValueObject_SameComponents_AreEqual()
    {
        var a = UserName.Create("leo").Value;
        var b = UserName.Create("leo").Value;

        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a != b);
    }

    [Fact]
    public void ValueObject_DifferentComponents_AreNotEqual()
    {
        var a = UserName.Create("leo").Value;
        var b = UserName.Create("leo2").Value;

        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void ValueObject_TrimsInput()
    {
        Assert.Equal("leo", UserName.Create("  leo  ").Value.Value);
    }

    [Fact]
    public void ValueObject_RejectsEmptyInput()
    {
        var result = UserName.Create("   ");

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user_name.empty", result.Error.Code);
    }

    [Fact]
    public void ValueObject_RejectsOverlongInput()
    {
        var result = UserName.Create(new string('x', UserName.MaxLength + 1));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user_name.too_long", result.Error.Code);
    }
}
