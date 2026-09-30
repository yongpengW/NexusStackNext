using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 内存 Outbox 存储：让整条链路**不需要数据库就能被构造与验证**。
///
/// <para><b>它的存在理由不是"临时替代"，而是补上一处不对称。</b>
/// 收件箱有 <c>InMemoryInboxStore</c>，发件箱却一直没有对应物——
/// 于是 <c>AddNexusStackInfrastructure</c> 注册的 <c>OutboxPublisher</c> 依赖一个
/// **零实现**的端口。在 Production 下没人验证得出来；在 Development 下
/// （也就是 <c>dotnet run</c> 的默认环境）<c>ValidateOnBuild</c> 当场抛
/// <c>Unable to resolve service for type 'IOutboxStore'</c>，进程起不来。</para>
///
/// <para>EF Core 版接入时（票据 19）应当**新增**而不是替换它——
/// 测试仍然需要内存版；这与其余五个上下文里内存适配器的处置一致。</para>
///
/// <para><b>线程安全</b>：用锁保护那张表。投递循环与聚合持久化会从不同请求里碰它。</para>
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly Lock _gate = new();
    private readonly List<OutboxEntry> _entries = [];

    /// <summary>当前全部记录，按写入顺序。<b>供诊断与测试观察，不参与投递。</b></summary>
    public IReadOnlyList<OutboxEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// 写入一条待投递记录。
    ///
    /// <para><b>刻意不在 <see cref="IOutboxStore"/> 上。</b>那个端口说得很清楚：
    /// 写入是**聚合持久化**的一部分（同一事务里收集领域事件并落 Outbox），不是投递器的职责。
    /// 给投递器一个"写"的口子只会让"同事务"这条保证变得可疑。</para>
    ///
    /// <para>将来 EF Core 版由拦截器在 <c>SaveChanges</c> 时调用它对应的东西。</para>
    /// </summary>
    /// <param name="entry">待投递记录。</param>
    public void Enqueue(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(
            [
                .. _entries
                    .Where(static entry => entry.IsPending)
                    .Where(entry => entry.NextAttemptAt is null || entry.NextAttemptAt <= now)
                    .OrderBy(static entry => entry.OccurredAt)
                    .Take(batchSize),
            ]);
        }
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Replace(id, entry => entry.MarkDelivered(now));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(
        Guid id,
        string failure,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        Replace(id, entry => entry.RecordFailure(failure, nextAttemptAt));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(
        Guid id,
        string failure,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Replace(id, entry => entry.MarkDeadLettered(failure, now));
        return Task.CompletedTask;
    }

    private void Replace(Guid id, Func<OutboxEntry, OutboxEntry> update)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);

            // 找不到就抛，不静默忽略：投递器报告"投递成功"而记录不存在，
            // 意味着状态已经对不上了，那必须响亮。
            if (index < 0)
            {
                throw new InvalidOperationException($"Outbox 记录不存在：{id}。");
            }

            _entries[index] = update(_entries[index]);
        }
    }
}
