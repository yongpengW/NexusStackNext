using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>处理一个集成事件。返回 <c>false</c> 表示这次没处理成功，该重试。</summary>
public interface IIntegrationEventProcessor
{
    /// <summary>它处理哪个事件（与订阅里的 <c>EventName</c> 对应）。</summary>
    string EventName { get; }

    /// <summary>处理事件。</summary>
    /// <param name="envelope">事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理成功为 <c>true</c>。</returns>
    Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// 消费一个订阅的消息。
///
/// <para><b>三件事都在这里，而且每一件都对应参照仓库的一处缺陷：</b></para>
///
/// <list type="number">
/// <item><b>手动 ACK。</b>自动 ACK 下，broker 在消息**交给**消费者时就认为它成功了——
/// 消费者随后的失败（进程崩、抛异常）会让消息永久消失。</item>
/// <item><b>先查 Inbox，失败再还名额。</b>至少一次投递是消息队列的常态，不是异常。
/// 重复的消息直接 ACK 跳过，业务只生效一次；而**这一步没做成时要把名额还回去**，
/// 否则重投会被当成重复而跳过——重试与死信就都成了摆设（三段式的第二段）。</item>
/// <item><b>失败按档位重投。</b>参照仓库没有 DLQ 消费者——死信队列会静默堆积，
/// 而"堆积"与"没人发消息"在监控上看起来一样。</item>
/// </list>
///
/// <para><b>通道恢复是第四件。</b><c>ChannelShutdownAsync</c> 与
/// <c>CallbackExceptionAsync</c> 都会触发重建——参照仓库没有任何通道级恢复处理，
/// 于是 broker 抖一次之后，那个消费者就永远不再消费了，而进程还活着。</para>
/// </summary>
public sealed class RabbitMqConsumer : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly EventTopology _topology;
    private readonly EventSubscription _subscription;
    private readonly IIntegrationEventProcessor _handler;
    private readonly IInboxStore _inbox;
    private readonly Func<DateTimeOffset> _clock;

    private IConnection? _connection;
    private IChannel? _channel;
    private string? _consumerTag;

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
    /// <param name="inbox">去重用的 Inbox。</param>
    /// <param name="clock">时钟；测试里可替换。</param>
    public RabbitMqConsumer(
        RabbitMqOptions options,
        EventTopology topology,
        EventSubscription subscription,
        IIntegrationEventProcessor handler,
        IInboxStore inbox,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(inbox);

        _options = options;
        _topology = topology;
        _subscription = subscription;
        _handler = handler;
        _inbox = inbox;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>已处理的消息数（含跳过重复）。</summary>
    public int HandledCount { get; private set; }

    /// <summary>被判定为重复而跳过的消息数。</summary>
    public int DuplicateCount { get; private set; }

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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or AlreadyClosedException or System.Net.Sockets.SocketException)
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
            ClientProvidedName = $"{_options.ClientName}-{_subscription.ConsumerName}",
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // 通道出问题时**把链接置空**：外层循环会重建。
        // 不这么做的话，`IChannel` 会一直是个"已关闭但非 null"的对象，
        // 于是消费者挂在一个死通道上，安静地什么都不做。
        _channel.ChannelShutdownAsync += (_, args) =>
        {
            RecoveryCount++;
            _channel = null;

            // **叫醒主循环。** 见 `_channelLost` 的说明——不叫醒的话，
            // 重建那段代码永远不会被执行，而"不执行"与"执行了但没用"看起来一样。
            _channelLost.TrySetResult();
            _ = args;
            return Task.CompletedTask;
        };

        // 一次只取一条：消费失败时要保证**只有这一条**被重投。
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken)
            .ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, deliver) =>
        {
            await HandleDeliveryAsync(deliver, cancellationToken).ConfigureAwait(false);
        };

        _consumerTag = await _channel.BasicConsumeAsync(
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

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs deliver, CancellationToken cancellationToken)
    {
        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        var envelope = ReadEnvelope(deliver);

        if (envelope is null)
        {
            // 解析不了的消息**不能重投**：重投它还是解析不了，会无限循环。
            // 直接进死信——那里有人看着。
            await MoveToAsync(channel, _subscription.DeadLetterQueueName, deliver, attempts: 0, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // **先查 Inbox。** 至少一次投递是常态：同一条消息被投两次不是异常。
        var isNew = await _inbox.TryBeginProcessingAsync(
            _subscription.ConsumerName,
            envelope.EventName,
            envelope.MessageId,
            _clock(),
            cancellationToken).ConfigureAwait(false);

        if (!isNew)
        {
            DuplicateCount++;
            await channel.BasicAckAsync(deliver.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
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
            handled = false;
        }

        var attempts = ConsumePolicy.ReadAttempts(ReadHeaders(deliver));
        var decision = ConsumePolicy.Decide(handled, attempts, _subscription);

        if (!handled)
        {
            // **失败删键**（幂等三段式的第二段）。
            //
            // 上面占掉的名额必须还回去，否则重投到达时会被判成重复而 ACK 跳过：
            // 重试档位永远轮不到，处理器的失败也永远到不了死信队列——
            // 而计数器还在显示"重试过"。三步里缺这一步，整条重试链是安静的死的。
            //
            // 放在搬运**之前**：搬运成功就 ACK 了，之后没有第二次机会；
            // 而搬运失败时消息会自己回来，那时名额已经还了，正是我们要的。
            await _inbox
                .ReleaseAsync(_subscription.ConsumerName, envelope.EventName, envelope.MessageId, cancellationToken)
                .ConfigureAwait(false);
        }

        switch (decision.Outcome)
        {
            case DeliveryOutcome.Ack:
                HandledCount++;
                await channel.BasicAckAsync(deliver.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
                break;

            case DeliveryOutcome.Retry:
                RetriedCount++;
                await MoveToAsync(channel, decision.RetryQueueName!, deliver, attempts, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                RetriedCount++;
                await MoveToAsync(channel, _subscription.DeadLetterQueueName, deliver, attempts, cancellationToken)
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
        int attempts,
        CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = deliver.BasicProperties.MessageId,
            ContentType = deliver.BasicProperties.ContentType,
            Type = deliver.BasicProperties.Type,
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

            return new EventEnvelope
            {
                MessageId = Guid.TryParse(deliver.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid(),
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
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
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
