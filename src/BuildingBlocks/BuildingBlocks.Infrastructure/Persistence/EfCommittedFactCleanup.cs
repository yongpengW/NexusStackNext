using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>只清理代码指定的审计事件副本，所有 SQL 只访问所属上下文。</summary>
/// <typeparam name="TContext">事实所属上下文。</typeparam>
/// <param name="context">所属数据库。</param>
/// <param name="eventName">模块代码声明的审计契约名称。</param>
/// <param name="options">本上下文维护策略。</param>
/// <param name="clock">服务端时钟。</param>
public sealed class EfCommittedFactCleanup<TContext>(TContext context, string eventName, CommittedFactCleanupOptions options, IClock clock)
    : ICommittedFactCleanup where TContext : NexusStackDbContext
{
    /// <inheritdoc />
    public Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (eventName.Length > 200) { throw new InvalidOperationException("事实事件名称超出允许长度。"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("事实副本清理必须使用独立的维护作用域。");
        }
        var schema = context.Schema.Replace("\"", "\"\"", StringComparison.Ordinal);
        var sql = $"""
            DELETE FROM "{schema}".outbox AS target
            USING (
                SELECT "Id" FROM "{schema}".outbox
                WHERE "EventName" = @event AND "DeliveredAt" <= @cutoff AND "DeadLetteredAt" IS NULL
                ORDER BY "DeliveredAt", "Id" LIMIT @batch FOR UPDATE SKIP LOCKED
            ) AS eligible
            WHERE target."Id" = eligible."Id"
            """;
        // 单条语句原子完成选择与删除；不保存同作用域业务变更，也不触发新的业务事实。
        return context.Database.ExecuteSqlRawAsync(sql,
            [new NpgsqlParameter("event", eventName), new NpgsqlParameter("cutoff", clock.UtcNow.Subtract(options.DeliveredRetention)),
                new NpgsqlParameter("batch", options.BatchSize)], cancellationToken);
    }
}
