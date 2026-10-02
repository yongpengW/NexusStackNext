using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>手动 ACK 的消息运输器；接纳事务及幂等由应用处理器拥有。</summary>
public sealed class RabbitMqConsumer : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly EventSubscription _subscription;
    private readonly IIntegrationEventProcessor _handler;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// 通道关闭时用来**叫醒**主循环的信号。
    ///
    /// <para><b>没有它，恢复就是假的。</b>第一版把 <c>_channel</c> 置空，
    /// 但主循环还挂在 <c>Task.Delay(Timeout.Infinite)</c> 上——它**永远不会**走到重建那一步。
    /// 于是通道一断，消费者就安静地什么都不做，而进程活着、健康检查还是绿的。</para>
    /// </summary>
    private TaskCompletionSource _channelLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>创建消费者。</summary>
    /// <param name="options">连接配置。</param>
    /// <param name="topology">拓扑。</param>
    /// <param name="subscription">要消费的订阅。</param>
    /// <param name="handler">处理器。</param>
    public RabbitMqConsumer(
        RabbitMqOptions options,
        EventTopology topology,
        EventSubscription subscription,
        IIntegrationEventProcessor handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.EventName != subscription.EventName || !topology.Subscriptions.Contains(subscription))
        {
            throw new ArgumentException("处理器、订阅与拓扑不一致。", nameof(subscription));
        }

        _options = options;
        _subscription = subscription;
        _handler = handler;
    }

    /// <summary>已处理的消息数（含跳过重复）。</summary>
    public int HandledCount { get; private set; }

    /// <summary>进入重试或死信的消息数。</summary>
    public int RetriedCount { get; private set; }

    /// <summary>通道被重建过几次。</summary>
    public int RecoveryCount { get; private set; }

    /// <summary>开始消费，并一直保持到取消。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilCancelledAsync(cancellationToken).ConfigureAwait(false);
                // 回调/通道故障也要退避，避免坏消息在重连之间忙循环。
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or AlreadyClosedException or System.Net.Sockets.SocketException or OperationInterruptedException or IOException)
            {
                // broker 掉线：**等一会儿再来**，而不是让这个任务结束。
                // 结束了它就再也不会被拉起来——而进程还活着、健康检查还是绿的。
                RecoveryCount++;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConsumeUntilCancelledAsync(CancellationToken cancellationToken)
    {
        // 换一个新的信号：上一轮那个已经被触发过了，复用它会立刻返回、变成忙等。
        _channelLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await DisposeChannelAsync().ConfigureAwait(false);

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            AutomaticRecoveryEnabled = false,
            ClientProvidedName = $"{_options.ClientName}-{_subscription.ConsumerName}",
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken).ConfigureAwait(false);

        // 绑定本轮通道与信号。旧回调不能确认新通道的 delivery tag。
        var channel = _channel;
        var channelLost = _channelLost;
        channel.CallbackExceptionAsync += (_, _) => { channelLost.TrySetResult(); return Task.CompletedTask; };
        channel.ChannelShutdownAsync += (_, args) =>
        {
            RecoveryCount++;


            // **叫醒主循环。** 见 `_channelLost` 的说明——不叫醒的话，
            // 重建那段代码永远不会被执行，而"不执行"与"执行了但没用"看起来一样。
            channelLost.TrySetResult();
            _ = args;
            return Task.CompletedTask;
        };

        // 一次只取一条：消费失败时要保证**只有这一条**被重投。
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken)
            .ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, deliver) =>
        {
            await HandleDeliveryAsync(channel, deliver, cancellationToken).ConfigureAwait(false);
        };

        await _channel.BasicConsumeAsync(
            queue: _subscription.QueueName,
            // **手动 ACK。** 见类文档。
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // **谁先到都走**：外部取消（停机），或者通道断了（要重建）。
        // 只等取消的话，通道断了这个任务会一直挂着——而它看起来完全正常。
        await Task.WhenAny(
            Task.Delay(Timeout.Infinite, cancellationToken),
            _channelLost.Task).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs deliver, CancellationToken cancellationToken)
    {
        var envelope = ReadEnvelope(deliver);

        if (envelope is null)
        {
            // 解析不了的消息**不能重投**：重投它还是解析不了，会无限循环。
            // 直接进死信——那里有人看着。
            await MoveToAsync(channel, _subscription.DeadLetterQueueName, deliver, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool handled;
        try
        {
            handled = await _handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机中：**不 ACK 也不 Nack**，让消息回到队列，下次起来再处理。
            throw;
        }
        catch (Exception)
        {
            // 基础设施暂时故障：原消息仍由 broker 持有，不经过 TTL/DLX 搬运。
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await channel.BasicNackAsync(deliver.DeliveryTag, multiple: false, requeue: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var attempts = ConsumePolicy.ReadAttempts(ReadHeaders(deliver));
        var decision = ConsumePolicy.Decide(handled, attempts, _subscription);

        switch (decision.Outcome)
        {
            case DeliveryOutcome.Ack:
                HandledCount++;
                await channel.BasicAckAsync(deliver.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
                break;

            case DeliveryOutcome.Retry:
                RetriedCount++;
                await MoveToAsync(channel, decision.RetryQueueName!, deliver, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                RetriedCount++;
                await MoveToAsync(channel, _subscription.DeadLetterQueueName, deliver, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// 把消息**搬到**另一个队列，然后 ACK 原来那条。
    ///
    /// <para><b>为什么是"发布 + ACK"而不是"Nack 让它自己进死信"。</b>
    /// Nack 走的是队列自己的死信配置，路由键与消息头都不是我们能改的——
    /// 而重试需要带上**递增的档位**（`nexusstack-attempts`），否则第二次重试会回到第一档，
    /// 于是"三次重试"变成"无限重试"。</para>
    ///
    /// <para>路径经过**默认交换机**（空名）：每个队列都隐式绑定在它上面，
    /// 路由键就是队列名。这也是为什么重试队列必须先被声明出来。</para>
    /// </summary>
    private static async Task MoveToAsync(
        IChannel channel,
        string queue,
        BasicDeliverEventArgs deliver,
        CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = deliver.BasicProperties.MessageId,
            ContentType = deliver.BasicProperties.ContentType,
            Type = deliver.BasicProperties.Type,
            CorrelationId = deliver.BasicProperties.CorrelationId,
            Timestamp = deliver.BasicProperties.Timestamp,
            // `WithIncrementedAttempts` 返回只读字典，而 `BasicProperties.Headers` 要的是可变接口——
            // 复制一份。它同时是一道保护：**不改动调用方传进来的那个字典**。
            Headers = new Dictionary<string, object?>(ConsumePolicy.WithIncrementedAttempts(ReadHeaders(deliver)), StringComparer.Ordinal),
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queue,
            mandatory: true,
            basicProperties: properties,
            body: deliver.Body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ACK 在**发布成功之后**：反过来的话，发布失败就等于消息丢了。
        await channel.BasicAckAsync(deliver.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
    }

    private EventEnvelope? ReadEnvelope(BasicDeliverEventArgs deliver)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(deliver.Body.Span);
            var headers = ReadHeaders(deliver);

            var eventName = headers.TryGetValue("EventName", out var raw) && raw is not null
                ? Encoding.UTF8.GetString(raw as byte[] ?? Encoding.UTF8.GetBytes(raw.ToString()!))
                : deliver.BasicProperties.Type ?? _subscription.EventName;

            if (!Guid.TryParse(deliver.BasicProperties.MessageId, out var id) || id == Guid.Empty
                || eventName != _subscription.EventName) { return null; }
            return new EventEnvelope
            {
                MessageId = id,
                EventName = eventName,
                Payload = payload,
                OccurredAt = DateTimeOffset.FromUnixTimeSeconds(deliver.BasicProperties.Timestamp.UnixTime),
            };
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ReadHeaders(BasicDeliverEventArgs deliver) =>
        deliver.BasicProperties.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(deliver.BasicProperties.Headers, StringComparer.Ordinal);

    private async ValueTask DisposeChannelAsync()
    {
        if (_channel is not null)
        {
            var channel = _channel;
            _channel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    /// <summary>停止消费。</summary>
    /// <returns>任务。</returns>
    public async ValueTask DisposeAsync() => await DisposeChannelAsync().ConfigureAwait(false);
}
