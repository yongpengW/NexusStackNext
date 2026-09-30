using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>处理无返回值命令。</summary>
/// <typeparam name="TCommand">命令类型。</typeparam>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    /// <summary>执行命令。</summary>
    /// <param name="command">命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功或失败。</returns>
    Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>处理有返回值命令。</summary>
/// <typeparam name="TCommand">命令类型。</typeparam>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>执行命令。</summary>
    /// <param name="command">命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功并带值，或失败。</returns>
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>处理查询。</summary>
/// <typeparam name="TQuery">查询类型。</typeparam>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>执行查询。</summary>
    /// <param name="query">查询。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功并带值，或失败。</returns>
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
