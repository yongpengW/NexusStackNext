using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>所属上下文的 Outbox 投递存储；确认后的状态不可被迟到的失败覆盖。</summary>
/// <typeparam name="TContext">拥有此 Outbox 的上下文。</typeparam>
/// <param name="context">本地数据库。</param>
public sealed class EfOutboxStore<TContext>(TContext context) : IOutboxStore where TContext : NexusStackDbContext
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return await context.Outbox.AsNoTracking()
            .Where(x => x.DeliveredAt == null && x.DeadLetteredAt == null && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsDelivered ? entry
            : entry.MarkDelivered(ConfirmationTime(now)) with { DeadLetteredAt = null, NextAttemptAt = null }, cancellationToken);

    private static DateTimeOffset ConfirmationTime(DateTimeOffset now)
    {
        // PostgreSQL stores whole microseconds. Rounding down could expire a confirmed copy before its minimum retention.
        // Preserve the first ACK and use the earliest representable instant that is not earlier than that ACK.
        var remainder = now.UtcTicks % 10;
        return (remainder == 0 ? now : now.AddTicks(10 - remainder)).ToUniversalTime();
    }

    /// <inheritdoc />
    public Task<bool> MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsPending && entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);

    /// <inheritdoc />
    public Task<bool> MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsPending && entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task<bool> UpdateAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        // 使用重试策略的上下文必须把“开事务至提交”整体交给策略；否则首次确认即抛异常，
        // 每轮都会重复发送第一条而无法继续后面的消息。
        return context.Database.CurrentTransaction is not null
            ? UpdateInTransactionAsync(id, update, cancellationToken)
            : context.Database.CreateExecutionStrategy().ExecuteAsync(() => UpdateInTransactionAsync(id, update, cancellationToken));
    }

    private async Task<bool> UpdateInTransactionAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        // 锁住最新状态再经跟踪器保存：既不丢并发更新，也不绕过 SaveChanges 拦截器。
        await using var transaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        var schema = context.Schema.Replace("\"", "\"\"", StringComparison.Ordinal);
        var sql = $"SELECT * FROM \"{schema}\".outbox WHERE \"Id\" = {{0}} FOR UPDATE";
        var entry = (await context.Outbox.FromSqlRaw(sql, id).ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (entry is null) { return false; }
        // 同一个作用域可能已跟踪它；拿锁后刷新，避免 EF 的身份映射返回旧状态。
        await context.Entry(entry).ReloadAsync(cancellationToken).ConfigureAwait(false);
        var updated = update(entry);
        if (updated == entry) { return false; }
        context.Entry(entry).CurrentValues.SetValues(updated);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }
        return true;
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

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        // 同一条 SQL 上的对称操作：删掉那个占位的名额，让重投能再进来一次。
        // 删不到行不是错误——"已成功留键"之后业务不会走到这里。
        var sql = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            DELETE FROM "{context.Schema}"."inbox"
            WHERE "ConsumerName" = @consumer AND "EventName" = @event AND "MessageId" = @message
            """);

        Npgsql.NpgsqlParameter[] parameters =
        [
            new("consumer", consumerName),
            new("event", eventName),
            new("message", messageId),
        ];

        await context.Database
            .ExecuteSqlRawAsync(sql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
