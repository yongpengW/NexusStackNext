using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>将所属上下文生成的事实加入同一次保存，并清理保存失败或取消后的暂存消息。</summary>
/// <typeparam name="TContext">唯一有权生成这些事实的持久化上下文。</typeparam>
public abstract class CommittedFactInterceptor<TContext> : SaveChangesInterceptor where TContext : NexusStackDbContext
{
    private readonly List<(TContext Context, OutboxEntry Entry)> _staged = [];

    /// <summary>根据本上下文的原始值与待提交值生成完整消息批次，不修改业务状态。</summary>
    /// <param name="context">所属上下文。</param>
    /// <returns>已经完成序列化的消息；动作、字段和隐私策略由上下文自己定义。</returns>
    protected abstract IReadOnlyList<OutboxEntry> CreateFacts(TContext context);

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stage(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Stage(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Forget(eventData.Context, detach: false);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Forget(eventData.Context, detach: false);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Forget(eventData.Context, detach: true);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Forget(eventData.Context, detach: true);
    }

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesCanceled(eventData);
        return Task.CompletedTask;
    }

    private void Stage(DbContext? context)
    {
        if (context is not TContext owned) { return; }
        owned.ChangeTracker.DetectChanges();
        // 先完成整个批次的构造/序列化，再附加消息，避免构造中断留下半批次。
        var facts = CreateFacts(owned);
        foreach (var fact in facts)
        {
            owned.Outbox.Add(fact);
            _staged.Add((owned, fact));
        }
    }

    private void Forget(DbContext? context, bool detach)
    {
        foreach (var staged in _staged.Where(item => ReferenceEquals(item.Context, context)))
        {
            if (detach && staged.Context.Entry(staged.Entry).State == EntityState.Added)
            {
                staged.Context.Entry(staged.Entry).State = EntityState.Detached;
            }
        }
        _staged.RemoveAll(item => ReferenceEquals(item.Context, context));
    }
}
