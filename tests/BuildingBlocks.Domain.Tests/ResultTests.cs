namespace NexusStackNext.BuildingBlocks.Domain.Tests;

/// <summary><see cref="Result"/> 的构造约束与取值语义。</summary>
public sealed class ResultTests
{
    [Fact]
    public void Success_CarriesNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.True(result.Error.IsNone);
    }

    [Fact]
    public void Failure_CarriesTheError()
    {
        var error = new Error("identity.not_found", "用户不存在。");

        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.not_found", result.Error.Code);
    }

    [Fact]
    public void Factories_CannotProduceContradictoryResults()
    {
        // 成功不携带错误、失败必须携带错误——这两条由构造函数强制，
        // 因此公开工厂无法造出自相矛盾的结果。
        Assert.True(Result.Success().Error.IsNone);
        Assert.False(Result.Failure(new Error("x", "y")).Error.IsNone);
        Assert.True(Result.Success(1).Error.IsNone);
        Assert.False(Result.Failure<int>(new Error("x", "y")).Error.IsNone);
    }

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void GenericFailure_AccessingValueThrows()
    {
        var result = Result.Failure<int>(new Error("identity.invalid", "不合法。"));

        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal(0, result.ValueOrDefault);
    }

    [Fact]
    public void Error_ToString_IsReadable()
    {
        Assert.Equal("OK", Error.None.ToString());
        Assert.Equal("a: b", new Error("a", "b").ToString());
    }
}