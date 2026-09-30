using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Transactions;
using NexusStackNext.BuildingBlocks.Application.Validation;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>
/// 分发器。管线顺序是<b>固定且显式</b>的，不是可插拔的行为链：
/// <list type="number">
///   <item>校验——在事务外，失败即短路，处理器根本不会被调用；</item>
///   <item>开事务——仅命令；查询不经过这一步；</item>
///   <item>执行处理器；</item>
///   <item>成功才 <c>SaveChanges</c>——失败时未刷新的改动随事务结束丢弃；</item>
///   <item>提交；任何异常都会回滚。</item>
/// </list>
/// 之所以不做成"行为链框架"：那会引入一整套抽象，而这里只有三步，显式写出来读得更清楚，
/// 也更容易证明"失败不落库"。
/// </summary>
/// <remarks>
/// <b>反射只在两处，而且都收在包装器里：</b>
/// <list type="bullet">
///   <item>按请求的运行时类型构造处理器包装器——闭合类型由下面的缓存持有，每个请求类型只付一次构造开销；</item>
///   <item>按请求的运行时类型解析校验器——同样缓存。</item>
/// </list>
/// 调用方与处理器都不接触反射。这里刻意不用 <c>dynamic</c>：它把类型错误推迟到运行时，
/// 且异常信息很难读。注册仍然是全显式的（见 <c>ApplicationServiceCollectionExtensions</c>），
/// 没有任何程序集扫描。
/// </remarks>
/// <param name="serviceProvider">用于解析处理器、校验器与工作单元。</param>
internal sealed class Sender(IServiceProvider serviceProvider) : ISender
{
    /// <inheritdoc />
    public Task<Result> SendAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Validate(command) is { } error
            ? Task.FromResult(Result.Failure(error))
            : ExecuteVoidCommandAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TResult>> SendAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Validate(command) is { } error
            ? Task.FromResult(Result.Failure<TResult>(error))
            : ExecuteCommandAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (Validate(query) is { } error)
        {
            return Task.FromResult(Result.Failure<TResult>(error));
        }

        var wrapper = HandlerWrapperCache<VoidOrValueWrapper<TResult>>
            .For(query.GetType(), typeof(TResult), typeof(QueryHandlerWrapper<,>));

        // 查询刻意不经过工作单元：读路径不需要事务。
        return wrapper.InvokeAsync(query, serviceProvider, cancellationToken);
    }

    private async Task<Result> ExecuteVoidCommandAsync(ICommand command, CancellationToken cancellationToken)
    {
        var wrapper = VoidCommandWrapperCache.For(command.GetType());
        var unitOfWork = serviceProvider.GetService<IUnitOfWork>();

        if (unitOfWork is null)
        {
            return await wrapper.InvokeAsync(command, serviceProvider, cancellationToken).ConfigureAwait(false);
        }

        return await unitOfWork
            .ExecuteInTransactionAsync(
                async token =>
                {
                    var result = await wrapper.InvokeAsync(command, serviceProvider, token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                    }

                    return result;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<TResult>> ExecuteCommandAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken)
    {
        var wrapper = HandlerWrapperCache<VoidOrValueWrapper<TResult>>
            .For(command.GetType(), typeof(TResult), typeof(CommandHandlerWrapper<,>));
        var unitOfWork = serviceProvider.GetService<IUnitOfWork>();

        if (unitOfWork is null)
        {
            return await wrapper.InvokeAsync(command, serviceProvider, cancellationToken).ConfigureAwait(false);
        }

        return await unitOfWork
            .ExecuteInTransactionAsync(
                async token =>
                {
                    var result = await wrapper.InvokeAsync(command, serviceProvider, token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                    }

                    return result;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>解析并运行校验器；没有注册校验器就跳过。</summary>
    /// <param name="request">请求。</param>
    /// <returns>失败原因；通过则为 <c>null</c>。</returns>
    /// <summary>
    /// 找出第一个拒绝这个请求的校验器。
    ///
    /// <para><b>为什么要走一遍实现的接口。</b><c>IRequestValidator&lt;in TRequest&gt;</c> 的类型参数是逆变的，
    /// 它的文档也因此写着"可以为基类型注册一个校验器覆盖多个请求"。但**容器不按变体匹配**：
    /// <c>GetService&lt;IRequestValidator&lt;具体类型&gt;&gt;()</c> 只找**精确注册**，
    /// 于是一条注册在基接口上的校验器**永远不会被调用**——能力写在文档里，
    /// 而代码里从来不存在。</para>
    ///
    /// <para>这里显式走一遍：先按请求的精确类型找，再按它实现的每个接口找。
    /// 于是"一个校验器覆盖一类请求"从一个说法变成了事实
    /// （Identity 的 <c>IdentifiedRequestValidator</c> 就是这么用的）。</para>
    /// </summary>
    private Error? Validate(object request)
    {
        foreach (var candidate in ValidatorCandidates(request.GetType()))
        {
            var result = ValidatorWrapperCache.For(candidate).Validate(request, serviceProvider);

            if (result.IsFailure)
            {
                return result.Error;
            }
        }

        return null;
    }

    /// <summary>校验器的查找顺序：精确类型优先，然后是它实现的接口。</summary>
    private static IEnumerable<Type> ValidatorCandidates(Type requestType)
    {
        yield return requestType;

        foreach (var contract in requestType.GetInterfaces())
        {
            yield return contract;
        }
    }

    /// <summary>
    /// 双类型参数包装器（命令/查询 + 返回值）的缓存。
    /// <para>
    /// 缓存键是 <c>(请求类型, 开放包装器类型)</c>。必须带开放类型——同一个返回值类型下，
    /// 命令包装器与查询包装器是不同的闭合类型，只按请求类型做键会串。
    /// </para>
    /// </summary>
    /// <typeparam name="TWrapper">包装器基类型。</typeparam>
    private static class HandlerWrapperCache<TWrapper>
        where TWrapper : class
    {
        private static readonly ConcurrentDictionary<(Type Request, Type Open), TWrapper> Instances = new();

        public static TWrapper For(Type requestType, Type resultType, Type openWrapperType) =>
            Instances.GetOrAdd(
                (requestType, openWrapperType),
                static (key, result) =>
                    (TWrapper)Activator.CreateInstance(key.Open.MakeGenericType(key.Request, result))!,
                resultType);
    }

    /// <summary>单类型参数包装器（无返回值命令）的缓存。</summary>
    private static class VoidCommandWrapperCache
    {
        private static readonly ConcurrentDictionary<Type, VoidCommandHandlerWrapper> Instances = new();

        public static VoidCommandHandlerWrapper For(Type requestType) =>
            Instances.GetOrAdd(
                requestType,
                static type => (VoidCommandHandlerWrapper)Activator
                    .CreateInstance(typeof(VoidCommandHandlerWrapper<>).MakeGenericType(type))!);
    }

    /// <summary>校验器包装器的缓存。</summary>
    private static class ValidatorWrapperCache
    {
        private static readonly ConcurrentDictionary<Type, RequestValidatorWrapper> Instances = new();

        public static RequestValidatorWrapper For(Type requestType) =>
            Instances.GetOrAdd(
                requestType,
                static type => (RequestValidatorWrapper)Activator
                    .CreateInstance(typeof(RequestValidatorWrapper<>).MakeGenericType(type))!);
    }
}

/// <summary>值命令与查询共用的包装器基类——两者都是"请求 → <c>Result&lt;TResult&gt;</c>"。</summary>
/// <typeparam name="TResult">返回值类型。</typeparam>
internal abstract class VoidOrValueWrapper<TResult>
{
    public abstract Task<Result<TResult>> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>无返回值命令的包装器基类。</summary>
internal abstract class VoidCommandHandlerWrapper
{
    public abstract Task<Result> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>校验器包装器基类。</summary>
internal abstract class RequestValidatorWrapper
{
    public abstract Result Validate(object request, IServiceProvider serviceProvider);
}

/// <summary>把 <see cref="ICommandHandler{TCommand}"/> 包成可按 <see cref="object"/> 调用的形状。</summary>
/// <typeparam name="TCommand">命令类型。</typeparam>
internal sealed class VoidCommandHandlerWrapper<TCommand> : VoidCommandHandlerWrapper
    where TCommand : ICommand
{
    public override Task<Result> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
        serviceProvider.GetRequiredService<ICommandHandler<TCommand>>()
            .HandleAsync((TCommand)request, cancellationToken);
}

/// <summary>把 <see cref="ICommandHandler{TCommand,TResult}"/> 包成可按 <see cref="object"/> 调用的形状。</summary>
/// <typeparam name="TCommand">命令类型。</typeparam>
/// <typeparam name="TResult">返回值类型。</typeparam>
internal sealed class CommandHandlerWrapper<TCommand, TResult> : VoidOrValueWrapper<TResult>
    where TCommand : ICommand<TResult>
{
    public override Task<Result<TResult>> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
        serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>()
            .HandleAsync((TCommand)request, cancellationToken);
}

/// <summary>把 <see cref="IQueryHandler{TQuery,TResult}"/> 包成可按 <see cref="object"/> 调用的形状。</summary>
/// <typeparam name="TQuery">查询类型。</typeparam>
/// <typeparam name="TResult">返回值类型。</typeparam>
internal sealed class QueryHandlerWrapper<TQuery, TResult> : VoidOrValueWrapper<TResult>
    where TQuery : IQuery<TResult>
{
    public override Task<Result<TResult>> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
        serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>()
            .HandleAsync((TQuery)request, cancellationToken);
}

/// <summary>把可选的 <see cref="IRequestValidator{TRequest}"/> 包成可按 <see cref="object"/> 调用的形状。</summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
internal sealed class RequestValidatorWrapper<TRequest> : RequestValidatorWrapper
{
    public override Result Validate(object request, IServiceProvider serviceProvider) =>
        serviceProvider.GetService<IRequestValidator<TRequest>>() is { } validator
            ? validator.Validate((TRequest)request)
            : Result.Success();
}
