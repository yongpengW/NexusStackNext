using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

/// <summary>
/// 分发器的校验与分发行为。事务的持久化保证由各上下文的集成测试验证。
/// <para>
/// 注意所有调用点都只写 <c>SendAsync(command)</c> / <c>QueryAsync(query)</c>——
/// 没有显式类型参数、没有 <c>dynamic</c>。类型推断能工作，是因为参数声明成了请求的<b>接口</b>。
/// </para>
/// </summary>
public sealed class SenderTests
{
    [Fact]
    public async Task SendAsync_Command_DoesNotUseAnUnownedUnitOfWork()
    {
        using var host = new TestHost();

        var result = await host.Sender.SendAsync(new RegisterUserCommand("leo"));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["validate", "handler:RegisterUser"],
            host.Log.Entries);
    }

    [Fact]
    public async Task SendAsync_ValidationFailure_ShortCircuitsBeforeOpeningTransaction()
    {
        using var host = new TestHost();

        var result = await host.Sender.SendAsync(new RegisterUserCommand("   "));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user_name.empty", result.Error.Code);

        // 校验在事务外：没有 begin，处理器根本没被调用。
        Assert.Equal(["validate"], host.Log.Entries);
    }

    [Fact]
    public async Task SendAsync_HandlerThrows_Propagates()
    {
        using var host = new TestHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Sender.SendAsync(new ExplodingCommand()));

        Assert.Equal(["handler:Exploding"], host.Log.Entries);
        Assert.DoesNotContain("commit", host.Log.Entries);
        Assert.DoesNotContain("save", host.Log.Entries);
    }

    [Fact]
    public async Task SendAsync_FailureResult_DoesNotSaveChanges()
    {
        // 共享分发器不选择工作单元：事务在所属上下文的命令入口执行。
        using var host = new TestHost();

        var result = await host.Sender.SendAsync(new RegisterUserCommand("taken"));

        Assert.True(result.IsFailure);
        Assert.Equal("identity.user_name.taken", result.Error.Code);
        Assert.Equal(["validate", "handler:RegisterUser"], host.Log.Entries);
        Assert.DoesNotContain("save", host.Log.Entries);
    }

    [Fact]
    public async Task SendAsync_ValueCommand_InfersResultType_WithoutUsingAnUnownedUnitOfWork()
    {
        using var host = new TestHost();

        // TResult 由 CountValueCommand : ICommand<int> 推断，调用方不写类型参数。
        var result = await host.Sender.SendAsync(new CountValueCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        Assert.DoesNotContain("save", host.Log.Entries);
    }

    [Fact]
    public async Task QueryAsync_InfersResultType_AndDoesNotOpenTransaction()
    {
        using var host = new TestHost();

        var result = await host.Sender.QueryAsync(new CountUsersQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);

        // 读路径不开事务：日志里只有处理器本身。
        Assert.Equal(["handler:CountUsers"], host.Log.Entries);
    }

    [Fact]
    public async Task QueryAsync_UsesInjectedClock()
    {
        var fixedNow = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero);
        using var host = new TestHost(now: fixedNow);

        var result = await host.Sender.QueryAsync(new CurrentTimeQuery());

        Assert.Equal(fixedNow, result.Value);
    }

    [Fact]
    public async Task SendAsync_WithoutUnitOfWork_StillRunsHandler()
    {
        // 不变量 4 的事务由 IUnitOfWork 提供；没注册它时管线仍应可用（例如无持久化的场景）。
        using var host = new TestHost(withUnitOfWork: false);

        var result = await host.Sender.SendAsync(new RegisterUserCommand("leo"));

        Assert.True(result.IsSuccess);
        Assert.Equal(["validate", "handler:RegisterUser"], host.Log.Entries);
    }

    [Fact]
    public async Task SendAsync_UnregisteredHandler_FailsFast()
    {
        using var host = new TestHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Sender.SendAsync(new UnhandledCommand()));
    }

    [Fact]
    public async Task SendAsync_NullCommand_Throws()
    {
        using var host = new TestHost();

        await Assert.ThrowsAsync<ArgumentNullException>(() => host.Sender.SendAsync((ICommand)null!));
    }

    [Fact]
    public async Task QueryAsync_NullQuery_Throws()
    {
        using var host = new TestHost();

        await Assert.ThrowsAsync<ArgumentNullException>(() => host.Sender.QueryAsync((IQuery<int>)null!));
    }

    [Fact]
    public async Task SameRequestType_SharesOneCachedWrapper_AndStillBehavesIdentically()
    {
        using var host = new TestHost();

        var first = await host.Sender.QueryAsync(new CountUsersQuery());
        var second = await host.Sender.QueryAsync(new CountUsersQuery());

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(["handler:CountUsers", "handler:CountUsers"], host.Log.Entries);
    }

    [Fact]
    public async Task ValueResult_FromFailure_HasNoValue()
    {
        using var host = new TestHost();

        var failure = Result.Failure<int>(new Error("x", "y"));

        Assert.False(failure.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => failure.Value);

        var success = await host.Sender.QueryAsync(new CountUsersQuery());
        Assert.Equal(42, success.Value);
    }
}
