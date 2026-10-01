using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

/// <summary>记录执行顺序，用于断言事务边界与短路行为。</summary>
internal sealed class CallLog
{
    public List<string> Entries { get; } = [];

    public void Add(string entry) => Entries.Add(entry);
}

/// <summary>把 begin / save / commit / rollback 记进日志的工作单元替身。</summary>
internal sealed class RecordingUnitOfWork(CallLog log) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        log.Add("save");
        return Task.FromResult(1);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool>? shouldCommit = null,
        CancellationToken cancellationToken = default)
    {
        log.Add("begin");
        try
        {
            var result = await operation(cancellationToken);
            log.Add((shouldCommit?.Invoke(result) ?? true) ? "commit" : "rollback");
            return result;
        }
        catch
        {
            log.Add("rollback");
            throw;
        }
    }
}

internal sealed record RegisterUserCommand(string UserName) : ICommand;

internal sealed class RegisterUserValidator(CallLog log) : IRequestValidator<RegisterUserCommand>
{
    public Result Validate(RegisterUserCommand request)
    {
        log.Add("validate");
        return string.IsNullOrWhiteSpace(request.UserName)
            ? Result.Failure(new Error("identity.user_name.empty", "用户名不能为空。"))
            : Result.Success();
    }
}

internal sealed class RegisterUserHandler(CallLog log) : ICommandHandler<RegisterUserCommand>
{
    public Task<Result> HandleAsync(RegisterUserCommand command, CancellationToken cancellationToken = default)
    {
        log.Add("handler:RegisterUser");
        return Task.FromResult(
            command.UserName == "taken"
                ? Result.Failure(new Error("identity.user_name.taken", "用户名已被占用。"))
                : Result.Success());
    }
}

internal sealed record ExplodingCommand : ICommand;

internal sealed class ExplodingHandler(CallLog log) : ICommandHandler<ExplodingCommand>
{
    public Task<Result> HandleAsync(ExplodingCommand command, CancellationToken cancellationToken = default)
    {
        log.Add("handler:Exploding");
        throw new InvalidOperationException("处理器炸了。");
    }
}

internal sealed record FailingCommand : ICommand;

internal sealed class FailingHandler(CallLog log) : ICommandHandler<FailingCommand>
{
    public Task<Result> HandleAsync(FailingCommand command, CancellationToken cancellationToken = default)
    {
        log.Add("handler:Failing");
        return Task.FromResult(Result.Failure(new Error("test.failed", "业务失败。")));
    }
}

internal sealed record CountValueCommand : ICommand<int>;

internal sealed class CountValueHandler(CallLog log) : ICommandHandler<CountValueCommand, int>
{
    public Task<Result<int>> HandleAsync(CountValueCommand command, CancellationToken cancellationToken = default)
    {
        log.Add("handler:CountValue");
        return Task.FromResult(Result.Success(7));
    }
}

/// <summary>没有注册处理器的命令，用于验证"注册失败要立刻炸"。</summary>
internal sealed record UnhandledCommand : ICommand;

internal sealed record CountUsersQuery : IQuery<int>;

internal sealed class CountUsersHandler(CallLog log) : IQueryHandler<CountUsersQuery, int>
{
    public Task<Result<int>> HandleAsync(CountUsersQuery query, CancellationToken cancellationToken = default)
    {
        log.Add("handler:CountUsers");
        return Task.FromResult(Result.Success(42));
    }
}

internal sealed record CurrentTimeQuery : IQuery<DateTimeOffset>;

internal sealed class CurrentTimeHandler(IClock clock) : IQueryHandler<CurrentTimeQuery, DateTimeOffset>
{
    public Task<Result<DateTimeOffset>> HandleAsync(
        CurrentTimeQuery query,
        CancellationToken cancellationToken = default) => Task.FromResult(Result.Success(clock.UtcNow));
}

/// <summary>本命名空间下的事件。</summary>
internal sealed record UserRegisteredEvent(Guid UserId) : IntegrationEvent
{
    public override string EventName => "identity.user-registered.v1";
}

/// <summary>
/// 用真实容器组装被测对象。刻意走 <c>AddNexusStackApplication</c>，
/// 这样注册扩展本身也被测到，而不是绕开它手搓一个 IServiceProvider。
/// </summary>
internal sealed class TestHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public TestHost(bool withUnitOfWork = true, DateTimeOffset? now = null)
    {
        Log = new CallLog();

        var services = new ServiceCollection();
        services.AddSingleton(Log);
        services.AddSingleton<IClock>(new FixedClock(now ?? DateTimeOffset.UnixEpoch));
        if (withUnitOfWork)
        {
            services.AddScoped<IUnitOfWork, RecordingUnitOfWork>();
        }

        services.AddNexusStackApplication();
        services.AddCommandHandler<RegisterUserCommand, RegisterUserHandler>();
        services.AddRequestValidator<RegisterUserCommand, RegisterUserValidator>();
        services.AddCommandHandler<ExplodingCommand, ExplodingHandler>();
        services.AddCommandHandler<FailingCommand, FailingHandler>();
        services.AddCommandHandler<CountValueCommand, int, CountValueHandler>();
        services.AddQueryHandler<CountUsersQuery, int, CountUsersHandler>();
        services.AddQueryHandler<CurrentTimeQuery, DateTimeOffset, CurrentTimeHandler>();

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Sender = _scope.ServiceProvider.GetRequiredService<ISender>();
    }

    public CallLog Log { get; }

    public ISender Sender { get; }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
