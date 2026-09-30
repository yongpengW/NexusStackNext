using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Transactions;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> 的 EF Core 实现。
///
/// <para><b>它存在的全部理由是"执行策略与事务只能二选一"那条约束</b>
/// （见 <see cref="IUnitOfWork"/> 的文档注释）。参照仓库开了
/// <c>EnableRetryOnFailure()</c>，又在 <c>PermissionService</c> 里用裸
/// <c>BeginTransactionAsync()</c>，EF Core 直接抛
/// "The configured execution strategy 'X' does not support user-initiated transactions"，
/// 那条用例必然失败。</para>
///
/// <para>本仓的做法是<b>让执行策略包裹事务</b>：
/// <c>CreateExecutionStrategy()</c> 拿到策略，把"开事务 → 执行 → 提交"整段交给它。
/// 这样瞬时故障能自动重试，事务边界也不会被打断——重试时会重新开一个新事务，
/// 而不是试图复用已经废掉的那个。</para>
///
/// <para><b>推论：传入的操作必须可重复执行。</b>重试意味着它可能跑多次，
/// 因此处理器必须是幂等的或纯计算的，且不得有事务外的副作用
/// （这条写在 <see cref="IUnitOfWork"/> 上，这里再强调一次，因为它是重试的代价）。</para>
/// </summary>
/// <typeparam name="TContext">上下文类型。</typeparam>
/// <param name="context">上下文。</param>
public sealed class EfUnitOfWork<TContext>(TContext context) : IUnitOfWork
    where TContext : DbContext
{
    /// <summary>上下文——派生实现与测试会用到。</summary>
    public TContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => Context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // **顺序是关键**：先拿策略，再由策略去开事务。
        // 反过来（先 BeginTransaction 再让策略重试）正是参照仓库那条必然失败的写法。
        var strategy = Context.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var result = await operation(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        });
    }
}
