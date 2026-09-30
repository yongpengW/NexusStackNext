using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

/// <summary>
/// 票据 19 的头两条验收：**Outbox 与聚合写入同一事务**、**同一消息消费两次只生效一次**。
/// </summary>
public sealed class OutboxAndInboxTests
{
    private static async Task<long> CountAsync(PostgresTestDatabase database, string table)
    {
        await using var connection = await database.OpenAsync();

        // 表名不能参数化，但它是**测试里的常量**，不是外部输入。
        await using var command = new NpgsqlCommand($"select count(*) from {table}", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// **聚合写入失败时，Outbox 记录不产生**（票据 19 验收 1）。
    ///
    /// <para>这条是 Outbox 的全部意义所在。参照仓库是先 <c>Insert</c> 再 <c>Publish</c>，
    /// 发布失败就留下永久 Pending 的孤儿任务（<c>AsyncTaskService.cs:47-54</c>）——
    /// 或者反过来：消息发出去了，而业务改动回滚了。</para>
    ///
    /// <para><b>两次写入必须用两个上下文。</b>同一个上下文里插入重复主键会先在
    /// 变更跟踪器里撞车（"another instance with the same key is already being tracked"），
    /// 那是客户端异常，压根到不了数据库——也就验不到事务。</para>
    /// </summary>
    [PostgresFact]
    public async Task AggregateWriteFails_LeavesNoOutboxEntry()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();

        // 一、成功的写入：聚合与 Outbox 一起落库。
        await using (var first = await ProbeDatabase.CreateAsync(database))
        {
            first.Probes.Add(ProbeAggregate.Create(1, "第一条"));
            await first.SaveChangesAsync();
        }

        Assert.Equal(1L, await CountAsync(database, "probe_aggregate"));
        Assert.Equal(1L, await CountAsync(database, "outbox"));

        // 二、失败的写入：主键冲突。
        await using (var second = await ProbeDatabase.NewContextAsync(database))
        {
            second.Probes.Add(ProbeAggregate.Create(1, "重复主键"));

            await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        }

        // 三、**两样都不能变**。业务改了但消息没发、消息发了但业务没改，两种都不允许。
        Assert.Equal(1L, await CountAsync(database, "probe_aggregate"));
        Assert.Equal(1L, await CountAsync(database, "outbox"));
    }

    /// <summary>成功的写入应当把领域事件变成一条 Outbox 记录，且载荷与事件名正确。</summary>
    [PostgresFact]
    public async Task SuccessfulWrite_ProducesExactlyOneOutboxEntry()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);

        context.Probes.Add(ProbeAggregate.Create(42, "载荷检查"));
        await context.SaveChangesAsync();

        var entry = await context.Outbox.SingleAsync();

        Assert.Equal("probe.created.v1", entry.EventName);
        Assert.True(entry.IsPending, "新写入的记录应当是待投递状态。");
        Assert.Equal(0, entry.AttemptCount);
        Assert.Contains("42", entry.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// **同一消息消费两次，业务只生效一次**（票据 19 验收 2）。
    ///
    /// <para>两次消费都跑在**真实事务**里，因为这条保证的全部内容就是
    /// "标记已处理"与"业务已生效"同生共死。</para>
    /// </summary>
    [PostgresFact]
    public async Task SameMessageConsumedTwice_TakesEffectOnce()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);

        var unitOfWork = new EfUnitOfWork<ProbeDbContext>(context);
        var inbox = new EfInboxStore<ProbeDbContext>(context);

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var firstHandled = await unitOfWork.ExecuteInTransactionAsync(async cancellationToken =>
        {
            if (!await inbox.TryBeginProcessingAsync("probe-consumer", "probe.created.v1", messageId, now, cancellationToken))
            {
                return false;
            }

            context.Probes.Add(ProbeAggregate.Create(7, "业务生效一次"));
            await context.SaveChangesAsync(cancellationToken);
            return true;
        });

        Assert.True(firstHandled, "第一次收到该消息时应当处理。");
        Assert.Equal(1L, await CountAsync(database, "probe_aggregate"));

        // 同一条消息重投。**它必须仍然在一个可用的事务里被判为重复**——
        // 这是 `INSERT … ON CONFLICT DO NOTHING` 而非"捕获唯一键冲突"的理由：
        // 后者会让 PostgreSQL 把这个事务标记为 aborted，之后任何语句都失败。
        var secondHandled = await unitOfWork.ExecuteInTransactionAsync(async cancellationToken =>
        {
            if (!await inbox.TryBeginProcessingAsync("probe-consumer", "probe.created.v1", messageId, now, cancellationToken))
            {
                return false;
            }

            context.Probes.Add(ProbeAggregate.Create(8, "不该发生"));
            await context.SaveChangesAsync(cancellationToken);
            return true;
        });

        Assert.False(secondHandled, "第二次收到同一条消息时应当跳过。");
        Assert.Equal(1L, await CountAsync(database, "probe_aggregate"));
    }

    /// <summary>同一个消息 ID 在不同消费端**各自**登记——去重键里有消费端。</summary>
    [PostgresFact]
    public async Task SameMessageId_IsDeduplicatedPerConsumer()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);
        var inbox = new EfInboxStore<ProbeDbContext>(context);

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.True(await inbox.TryBeginProcessingAsync("consumer-a", "probe.created.v1", messageId, now));
        Assert.True(await inbox.TryBeginProcessingAsync("consumer-b", "probe.created.v1", messageId, now));
        Assert.False(await inbox.TryBeginProcessingAsync("consumer-a", "probe.created.v1", messageId, now));

        Assert.Equal(2L, await CountAsync(database, "inbox"));
    }

    /// <summary>
    /// **同一上下文的第二次 <c>SaveChanges</c> 不得重复入队。**
    ///
    /// <para>它守的是 <see cref="DomainEventOutboxInterceptor"/> 里那条取舍：
    /// 领域事件在**收集时**清空，而不是保存成功后。若不清空，
    /// 变更跟踪器里的聚合在第二次保存时仍然是 <c>Unchanged</c> 可见的，
    /// 于是同一批事件会**再入队一遍**——同一条消息被投递两次。</para>
    ///
    /// <para>这条测试是我在复盘反向验证时补的：原先那句"收集即清空避免重复"
    /// 写在注释里，而**没有任何东西守着它**。</para>
    /// </summary>
    [PostgresFact]
    public async Task SavingTwice_DoesNotEnqueueTheSameEventsTwice()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);

        var probe = ProbeAggregate.Create(1, "同一个上下文保存两次");
        context.Probes.Add(probe);
        await context.SaveChangesAsync();

        Assert.Equal(1L, await CountAsync(database, "outbox"));

        // 第二次保存：聚合这次是 Unchanged，不该再产出任何消息。
        await context.SaveChangesAsync();

        Assert.Equal(1L, await CountAsync(database, "outbox"));

        // 第三次也一样——这条防线不能只在前一次有效。
        probe.Rename("改个名再存");
        await context.SaveChangesAsync();

        Assert.Equal(1L, await CountAsync(database, "outbox"));
    }
}
