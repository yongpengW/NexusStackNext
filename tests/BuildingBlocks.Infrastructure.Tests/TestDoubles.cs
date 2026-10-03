using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>样本集成事件。</summary>
internal sealed record OrderPlaced(Guid OrderId) : IntegrationEvent
{
    public override string EventName => "ordering.order-placed.v1";
}

/// <summary>内存 Outbox 存储。刻意实现真实的语义（只读待投递、标记后不再返回）。</summary>
internal sealed class FakeOutboxStore : IOutboxStore
{
    private readonly List<OutboxEntry> _entries = [];

    public IReadOnlyList<OutboxEntry> Entries => _entries;

    public void Seed(params OutboxEntry[] entries) => _entries.AddRange(entries);

    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OutboxEntry>>(
        [
            .. _entries
                .Where(entry => entry.IsPending)
                .Where(entry => entry.NextAttemptAt is null || entry.NextAttemptAt <= now)
                .OrderBy(static entry => entry.OccurredAt)
                .Take(batchSize),
        ]);

    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Replace(id, entry => entry.MarkDelivered(now));
        return Task.CompletedTask;
    }

    public Task<bool> MarkFailedAsync(
        Guid id,
        string failure,
        DateTimeOffset nextAttemptAt,
        long expectedRetryRevision,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Replace(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry));
    }

    public Task<bool> MarkDeadLetteredAsync(
        Guid id,
        string failure,
        DateTimeOffset now,
        long expectedRetryRevision,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Replace(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry));
    }

    private bool Replace(Guid id, Func<OutboxEntry, OutboxEntry> update)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Outbox 记录不存在：{id}");
        }

        var before = _entries[index];
        var after = update(before);
        _entries[index] = after;
        return before != after;
    }
}

/// <summary>假事件总线：可配置哪些事件"不可路由"，也可配置直接抛异常。</summary>
internal sealed class FakeEventBus : IEventBus
{
    public List<EventEnvelope> Published { get; } = [];

    public HashSet<string> UnroutableEventNames { get; } = new(StringComparer.Ordinal);

    public Exception? ThrowOnPublish { get; set; }

    public Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (ThrowOnPublish is not null)
        {
            throw ThrowOnPublish;
        }

        Published.Add(envelope);

        return Task.FromResult(UnroutableEventNames.Contains(envelope.EventName)
            ? Result.Failure(MessagingErrors.Unroutable(envelope.EventName))
            : Result.Success());
    }
}

/// <summary>内存 Inbox 存储：用 HashSet.Add 的返回值表达"是否第一次"。</summary>
internal sealed class FakeInboxStore : IInboxStore
{
    private readonly HashSet<(string Consumer, string EventName, Guid MessageId)> _seen = [];

    public int RecordCount => _seen.Count;

    public Task<bool> TryBeginProcessingAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_seen.Add((consumerName, eventName, messageId)));

    public Task ReleaseAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        _seen.Remove((consumerName, eventName, messageId));
        return Task.CompletedTask;
    }
}
