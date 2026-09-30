using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// 在 <c>SaveChanges</c> 时把聚合抛出的领域事件收集进 Outbox。
///
/// <para><b>这是"同一事务"这条保证的落点。</b>Outbox 记录是在**同一次
/// <c>SaveChanges</c>** 里 <c>Add</c> 进去的，因此它与聚合的改动共用一个事务：
/// 业务改了但消息没发、消息发了但业务没改，两种都不可能发生。
/// 参照仓库是先 <c>Insert</c> 再 <c>Publish</c>，发布失败就留下永久 Pending 的孤儿任务
/// （<c>AsyncTaskService.cs:47-54</c>）。</para>
///
/// <para><b>它不认识任何具体事件。</b>领域事件 → 集成事件的转换交给
/// <see cref="IIntegrationEventMapper"/>（那一步属于应用层），这里只负责
/// "转换结果写进 Outbox"与"清空聚合上的事件列表"。</para>
///
/// <para><b>一个必须说清的取舍：事件在收集时就清空，不是保存成功后。</b>
/// 若等到成功后清空，执行策略的一次重试会**再次**触发 <c>SavingChanges</c>，
/// 于是同一条消息被入队两次（上一次那条虽被回滚，却仍在变更跟踪器里）。
/// 收集即清空避免了重复，代价是：**保存失败之后，调用方必须重新抛出领域事件**——
/// 这也正是 <c>IUnitOfWork</c> 那条"操作必须可重复执行"的契约所要覆盖的情形。</para>
/// </summary>
/// <param name="mapper">领域事件 → 集成事件。</param>
/// <param name="serializer">载荷序列化。</param>
public sealed class DomainEventOutboxInterceptor(
    IIntegrationEventMapper mapper,
    IIntegrationEventSerializer serializer) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // ToList()：下面会在遍历过程中 Add 新实体。
        var aggregates = context.ChangeTracker.Entries<IHasDomainEvents>().ToList();

        foreach (var entry in aggregates)
        {
            var domainEvents = entry.Entity.DomainEvents;

            if (domainEvents.Count == 0)
            {
                continue;
            }

            foreach (var domainEvent in domainEvents)
            {
                // **映射不到就不发布。** 默认不发布是安全的那一侧：
                // 领域事件常带内部细节，需要显式决定它是否成为对外契约。
                if (mapper.Map(domainEvent) is { } integrationEvent)
                {
                    context.Add(OutboxEntry.From(integrationEvent, serializer));
                }
            }

            entry.Entity.ClearDomainEvents();
        }
    }
}
