using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>一轮投递的结果，用于日志与指标。</summary>
/// <param name="Examined">本轮读取到的条数。</param>
/// <param name="Delivered">成功投递的条数。</param>
/// <param name="Retried">失败并已安排重试的条数。</param>
/// <param name="DeadLettered">进入死信的条数。</param>
public sealed record OutboxRunResult(int Examined, int Delivered, int Retried, int DeadLettered)
{
    /// <summary>本轮是否读到过待投递记录。</summary>
    public bool HasWork => Examined > 0;
}

/// <summary>
/// Outbox 投递器：把已落库但尚未发出的消息发到总线上。
/// <para>
/// 这是本项目里唯一负责"最终发出消息"的地方。它<b>不写 Outbox</b>（写入是持久化的一部分），
/// 只读待投递、发、再标记结果。
/// </para>
/// <para>
/// 单条失败不会中断整轮：一条坏消息不应该阻塞它后面的所有消息。
/// </para>
/// <para>
/// <b>没有"已发送但标记失败"的原子性</b>——这在分布式下无法做到。所以投递语义是
/// at-least-once，重复由消费端的 <see cref="IInboxStore"/> 吸收。这是有意的取舍，
/// 不是遗漏：声称 exactly-once 只会让人写出错误的假设。
/// </para>
/// </summary>
public sealed class OutboxPublisher
{
    private readonly IOutboxStore _store;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly OutboxDeliveryOptions _options;

    /// <summary>构造投递器。</summary>
    /// <param name="store">Outbox 存储。</param>
    /// <param name="eventBus">事件总线。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="options">投递策略。</param>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    public OutboxPublisher(
        IOutboxStore store,
        IEventBus eventBus,
        IClock clock,
        OutboxDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _store = store;
        _eventBus = eventBus;
        _clock = clock;
        _options = options;
    }

    /// <summary>执行一轮投递。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮结果。</returns>
    public async Task<OutboxRunResult> PublishPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _store
            .ReadPendingAsync(_options.BatchSize, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var delivered = 0;
        var retried = 0;
        var deadLettered = 0;

        foreach (var entry in pending)
        {
            var result = await PublishOneAsync(entry, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                await _store.MarkDeliveredAsync(entry.Id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                delivered++;
                continue;
            }

            var failure = result.Error.ToString();
            var attemptCount = entry.AttemptCount + 1;

            if (attemptCount >= _options.MaxAttempts)
            {
                await _store
                    .MarkDeadLetteredAsync(entry.Id, failure, _clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                deadLettered++;
            }
            else
            {
                await _store
                    .MarkFailedAsync(
                        entry.Id,
                        failure,
                        _clock.UtcNow + _options.BackoffFor(attemptCount),
                        cancellationToken)
                    .ConfigureAwait(false);
                retried++;
            }
        }

        return new OutboxRunResult(pending.Count, delivered, retried, deadLettered);
    }

    private async Task<Result> PublishOneAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await _eventBus.PublishAsync(entry.ToEnvelope(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不是投递失败：让它冒泡，不要把它记成一次失败尝试。
            throw;
        }
        catch (Exception exception)
        {
            // 总线实现抛异常 = 投递失败。转成失败结果，走正常的重试/死信路径。
            return Result.Failure(MessagingErrors.BrokerUnavailable(exception.Message));
        }
    }
}
