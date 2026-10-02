using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>命令执行的观察缝；事务仍由命令所属上下文管理。</summary>
public interface ICommandExecution
{
    /// <summary>执行一次命令，包括校验；观察实现不得改写返回值或吞掉执行异常。</summary>
    /// <typeparam name="TResponse">无值或有值的命令结果。</typeparam>
    /// <param name="command">命令；不是可自动序列化的日志载荷。</param>
    /// <param name="execute">必须且只能调用一次的命令入口。</param>
    /// <param name="cancellationToken">命令的取消令牌。</param>
    /// <returns>原命令结果。</returns>
    Task<TResponse> ExecuteAsync<TResponse>(object command, Func<Task<TResponse>> execute,
        CancellationToken cancellationToken = default) where TResponse : Result;
}

internal sealed class DirectCommandExecution : ICommandExecution
{
    public Task<TResponse> ExecuteAsync<TResponse>(object command, Func<Task<TResponse>> execute,
        CancellationToken cancellationToken = default) where TResponse : Result => execute();
}
