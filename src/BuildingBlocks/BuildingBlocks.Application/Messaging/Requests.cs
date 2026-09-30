namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>
/// 命令：改变状态。返回 <c>Result</c>。
/// <para>
/// 命令由分发器包在**一个事务**里执行（<c>AGENTS.md</c> 不变量 4）。
/// 处理器<b>不得</b>自己调用 <c>SaveChanges</c>——那是分发器的职责，
/// 否则"失败时不落库"这条保证会被绕过。
/// </para>
/// </summary>
public interface ICommand;

/// <summary>命令：改变状态并返回一个值。</summary>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface ICommand<TResult>;

/// <summary>查询：只读。<b>不开启事务</b>——读路径开事务只会平白增加连接占用与锁竞争。</summary>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface IQuery<TResult>;
