using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>
/// 用例入口。调用方只认识这个接口，不认识具体处理器。
/// <para>
/// <b>参数类型是请求的接口而不是具体类型</b>，这样类型推断才能工作：
/// <c>QueryAsync(new CountUsersQuery())</c> 里 <c>TResult</c> 由
/// <c>CountUsersQuery : IQuery&lt;int&gt;</c> 这一层基接口关系推出来。
/// </para>
/// <para>
/// 反过来，如果签名写成 <c>QueryAsync&lt;TQuery, TResult&gt;(TQuery query)</c>，调用方每次都得写
/// <c>QueryAsync&lt;CountUsersQuery, int&gt;(...)</c>——因为 C# 的类型推断<b>不看约束、也不看接口实现</b>，
/// 只从实参到形参做推断。这一条是被编译器教会的，不是设计时想到的。
/// </para>
/// </summary>
public interface ISender
{
    /// <summary>执行一个无返回值命令，整体包在一个事务里。</summary>
    /// <param name="command">命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功或失败。</returns>
    Task<Result> SendAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>执行一个有返回值命令，整体包在一个事务里。</summary>
    /// <typeparam name="TResult">返回值类型（由实参推断）。</typeparam>
    /// <param name="command">命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功并带值，或失败。</returns>
    Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    /// <summary>执行一个查询。<b>不开启事务</b>——读路径开事务只会平白增加连接占用与锁竞争。</summary>
    /// <typeparam name="TResult">返回值类型（由实参推断）。</typeparam>
    /// <param name="query">查询。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功并带值，或失败。</returns>
    Task<Result<TResult>> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}
