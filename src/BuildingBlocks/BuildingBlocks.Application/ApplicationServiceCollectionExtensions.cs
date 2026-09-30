using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Application.Validation;

namespace NexusStackNext.BuildingBlocks.Application;

/// <summary>
/// 应用层注册。
/// <para>
/// <b>全部显式注册，不做程序集扫描。</b>参照仓库用反射式大爆炸注册（<c>AddServices&lt;T&gt;</c> 扫接口），
/// 结果是谁都能被扫进来，边界不可见。这里每加一个处理器就得写一行——多写的那一行就是"这个服务由什么组成"的答案。
/// </para>
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>注册应用层基础设施：时钟与分发器。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddNexusStackApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<ISender, Sender>();
        return services;
    }

    /// <summary>注册无返回值命令处理器。</summary>
    /// <typeparam name="TCommand">命令类型。</typeparam>
    /// <typeparam name="THandler">处理器类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddCommandHandler<TCommand, THandler>(this IServiceCollection services)
        where TCommand : ICommand
        where THandler : class, ICommandHandler<TCommand>
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddScoped<ICommandHandler<TCommand>, THandler>();
    }

    /// <summary>注册有返回值命令处理器。</summary>
    /// <typeparam name="TCommand">命令类型。</typeparam>
    /// <typeparam name="TResult">返回值类型。</typeparam>
    /// <typeparam name="THandler">处理器类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddCommandHandler<TCommand, TResult, THandler>(this IServiceCollection services)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddScoped<ICommandHandler<TCommand, TResult>, THandler>();
    }

    /// <summary>注册查询处理器。</summary>
    /// <typeparam name="TQuery">查询类型。</typeparam>
    /// <typeparam name="TResult">返回值类型。</typeparam>
    /// <typeparam name="THandler">处理器类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddQueryHandler<TQuery, TResult, THandler>(this IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddScoped<IQueryHandler<TQuery, TResult>, THandler>();
    }

    /// <summary>注册请求校验器。没有注册校验器的请求不做校验。</summary>
    /// <typeparam name="TRequest">请求类型。</typeparam>
    /// <typeparam name="TValidator">校验器类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于链式调用。</returns>
    public static IServiceCollection AddRequestValidator<TRequest, TValidator>(this IServiceCollection services)
        where TValidator : class, IRequestValidator<TRequest>
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddScoped<IRequestValidator<TRequest>, TValidator>();
    }
}
