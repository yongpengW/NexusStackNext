using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// <see cref="IOutboxStore"/> 的 EF Core 实现。
///
/// <para><b>它刻意不提供"写入"</b>——接口上就没有 <c>Add</c>，写入由
/// <see cref="DomainEventOutboxInterceptor"/> 在同一次 <c>SaveChanges</c> 里完成。
/// 给投递器一个写口子会让"同事务"这条保证变得可疑。</para>
///
/// <para>因此这个类的每个方法都是**短事务**：读一批、标记一条，各自 <c>SaveChanges</c>。
/// 投递循环本来就不该长事务——它每几秒跑一次，占着连接只会拖累别人。</para>
/// </summary>
/// <typeparam name="TContext">上下文类型。</typeparam>
/// <param name="context">上下文。</param>
public sealed class EfOutboxStore<TContext>(TContext context) : IOutboxStore
    where TContext : NexusStackDbContext
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return await context.Outbox
            .Where(entry => entry.DeliveredAt == null
                && entry.DeadLetteredAt == null
                && (entry.NextAttemptAt == null || entry.NextAttemptAt <= now))
            // 按发生顺序投递：下游看到的次序才与事实发生的次序一致。
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
        => MutateAsync(id, entry => entry.MarkDelivered(now), cancellationToken);

    /// <inheritdoc />
    public Task MarkFailedAsync(
        Guid id,
        string failure,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
        => MutateAsync(id, entry => entry.RecordFailure(failure, nextAttemptAt), cancellationToken);

    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(
        Guid id,
        string failure,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => MutateAsync(id, entry => entry.MarkDeadLettered(failure, now), cancellationToken);

    /// <summary>
    /// 取出记录、套用一次状态迁移、存回。
    ///
    /// <para><see cref="OutboxEntry"/> 是不可变 record，状态迁移都是 <c>with</c> 表达式——
    /// 所以这里不能"就地改属性"，而要**替换跟踪器里的那个实体**。
    /// 直接改属性在 record 上编译不过，这反而让"状态迁移只有那几个方法"成为结构上的事实。</para>
    /// </summary>
    private async Task MutateAsync(
        Guid id,
        Func<OutboxEntry, OutboxEntry> transition,
        CancellationToken cancellationToken)
    {
        var entry = await context.Outbox
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            // 记录不在——说明另一个投递器已经处理过它，或它根本不属于这个上下文。
            // 不抛异常：投递循环不该因为一条记录消失而停摆。
            return;
        }

        context.Outbox.Update(transition(entry));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="IInboxStore"/> 的 EF Core 实现。
///
/// <para><b>调用方必须在与业务改动同一个事务里用它</b>（见 <c>IInboxStore</c> 的说明）。
/// 这个类只负责"试着登记一行，并如实回报是否第一次"——
/// 同事务这件事由 <c>IUnitOfWork.ExecuteInTransactionAsync</c> 保证，不在这里。</para>
///
/// <para><b>为什么用 <c>INSERT … ON CONFLICT DO NOTHING</c>，而不是"试着插入、捕获唯一键冲突"。</b>
/// 后者在**调用方的事务里根本不能用**：PostgreSQL 一旦有一条语句报错，
/// 整个事务就进入 aborted 状态，之后任何语句都会失败（<c>25P02: current transaction is aborted</c>）——
/// 而去重冲突恰恰是**常见路径**，不是异常路径。于是一条重复消息会把整个业务事务毒死。</para>
///
/// <para>改用 <c>ON CONFLICT</c> 之后：不抛异常、不需要 savepoint、不受 aborted 状态影响，
/// 而且"是否第一次"直接由**受影响行数**回答——那是数据库给的答案，不依赖任何时序。</para>
/// </summary>
/// <typeparam name="TContext">上下文类型。</typeparam>
/// <param name="context">上下文。</param>
public sealed class EfInboxStore<TContext>(TContext context) : IInboxStore
    where TContext : NexusStackDbContext
{
    /// <inheritdoc />
    public async Task<bool> TryBeginProcessingAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        // 表名要**显式带 schema**：这是原始 SQL，走不到 EF 的 `HasDefaultSchema`，
        // 而数据库连接的默认 schema 未必是上下文的 schema（测试里就肯定不是）。
        var sql = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            INSERT INTO "{context.Schema}"."inbox" ("ConsumerName", "EventName", "MessageId", "ReceivedAt")
            VALUES (@consumer, @event, @message, @received)
            ON CONFLICT DO NOTHING
            """);

        Npgsql.NpgsqlParameter[] parameters =
        [
            new("consumer", consumerName),
            new("event", eventName),
            new("message", messageId),
            new("received", now),
        ];

        var affected = await context.Database
            .ExecuteSqlRawAsync(sql, parameters, cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }
}
