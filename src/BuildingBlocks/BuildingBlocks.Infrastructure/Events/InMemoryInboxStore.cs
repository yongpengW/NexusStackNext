using System.Collections.Concurrent;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 内存收件箱。
/// <para>
/// <b>它的存在理由有两条。</b>一是让消息基座在没有数据库的环境里也能被端到端跑起来；
/// 二是按深模块的判据，<see cref="IInboxStore"/> 此前只有一个测试替身
/// ——"一个适配器只是假设的缝，两个才是真的缝"，这里给了它第二个。
/// </para>
/// <para>
/// <b>它与真实实现的关键差异必须说清</b>：生产实现里，
/// <c>TryBeginProcessingAsync</c> 与业务改动在**同一个事务**里提交。
/// 这里没有事务——它只保证"同一个进程内不重复处理"，
/// 进程崩溃后重投会再处理一次。开发与测试够用，**不能当生产实现**。
/// </para>
/// </summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<(string Consumer, string Event, Guid MessageId), DateTimeOffset> _seen = new();

    /// <inheritdoc />
    public Task<bool> TryBeginProcessingAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var key = (consumerName, eventName, messageId);

        // TryAdd 是原子的：并发重投时只有一个调用方拿到 true。
        return Task.FromResult(_seen.TryAdd(key, now));
    }
}
