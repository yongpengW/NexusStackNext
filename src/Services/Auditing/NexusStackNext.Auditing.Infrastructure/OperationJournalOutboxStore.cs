using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Auditing.Infrastructure;

// 限定在操作日志来源：复用投递协议，但不把数据库异常、broker 异常正文写入表或交给日志框架。
internal sealed class OperationJournalOutboxStore(IOutboxStore inner) : IOutboxStore
{
    private const string DeliveryFailed = "operation_journal.delivery_failed";

    public async Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadPendingAsync(batchSize, now, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException(DeliveryFailed); }
    }

    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => inner.MarkDeliveredAsync(id, now, cancellationToken), cancellationToken);

    public Task MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => inner.MarkFailedAsync(id, DeliveryFailed, nextAttemptAt, cancellationToken), cancellationToken);

    public Task MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => inner.MarkDeadLetteredAsync(id, DeliveryFailed, now, cancellationToken), cancellationToken);

    private static async Task SafelyAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException(DeliveryFailed); }
    }
}
