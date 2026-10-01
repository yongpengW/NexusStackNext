namespace NexusStackNext.BuildingBlocks.Application.Messaging;

/// <summary>
/// 命令：改变状态。返回 <c>Result</c>。
/// <para>
/// 事务由所属上下文的命令入口管理；共享分发器只负责校验和分发。
/// 处理器不自行调用 <c>SaveChanges</c>，保存及提交判据集中在上下文的事务边界。
/// </para>
/// </summary>
public interface ICommand;

/// <summary>命令：改变状态并返回一个值。</summary>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface ICommand<TResult>;

/// <summary>查询：只读。<b>不开启事务</b>——读路径开事务只会平白增加连接占用与锁竞争。</summary>
/// <typeparam name="TResult">返回值类型。</typeparam>
public interface IQuery<TResult>;
