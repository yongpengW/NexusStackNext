namespace NexusStackNext.BuildingBlocks.Application.Transactions;

/// <summary>
/// 工作单元。由基础设施层实现（EF Core），应用层只认识这个接口。
/// <para>
/// <b>关于重试与事务只能二选一的约束</b>（参照仓库在这里踩过坑）：
/// 它开了 <c>EnableRetryOnFailure()</c>，同时又在 <c>PermissionService</c> 里用裸
/// <c>BeginTransactionAsync()</c>，EF Core 直接抛"执行策略不支持用户自建事务"，那条用例必然失败。
/// </para>
/// <para>
/// 本项目选定的策略是：<b>保留执行策略，由执行策略包裹事务</b>——
/// 即实现方必须把整个事务放进 <c>IExecutionStrategy.ExecuteAsync</c> 里，
/// 而不是先开事务再让重试去撞它。这样瞬时故障（连接抖动、死锁牺牲者）能自动重试，
/// 事务边界也不会被打断。实现见票据 04。
/// </para>
/// <para>
/// 另一条约束：重试意味着处理器可能被执行多次，因此处理器必须是幂等的或纯计算的，
/// 且不得有事务外的副作用。
/// </para>
/// </summary>
public interface IUnitOfWork
{
    /// <summary>把当前跟踪到的改动刷进数据库；命令路径由所属上下文的提交边界调用。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响的行数。</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 在一个事务里执行操作。默认正常返回即提交；提交判据拒绝或抛异常则回滚并丢弃跟踪状态。
    /// </summary>
    /// <typeparam name="TResult">操作返回值类型。</typeparam>
    /// <param name="operation">要执行的操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="shouldCommit">返回值的提交判据；为空时正常返回即提交。</param>
    /// <returns>操作结果。</returns>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool>? shouldCommit = null,
        CancellationToken cancellationToken = default);
}
