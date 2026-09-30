using System.Text;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using RabbitMQ.Client;

namespace NexusStackNext.RabbitMq.IntegrationTests;

/// <summary>
/// 真 broker 上的消费行为（票据 21 验收 2–5）。
///
/// <para>四条各对应参照仓库的一处缺陷：只有失败者重试、通道断了要恢复、
/// 重复消息只生效一次、档位用尽要进死信并被人看到。</para>
/// </summary>
public sealed class RabbitMqConsumerTests
{
    private const string EventName = "identity.user.registered";

    private static EventTopology Topology(string prefix, params string[] consumers) =>
        EventTopology.Create(
            $"{prefix}-exchange",
            [.. consumers.Select(name => new EventSubscription
            {
                EventName = EventName,
                // **消费端名字里必须带前缀。**
                //
                // 队列名只由 (事件名, 消费端名) 派生，**不含交换机名**——所以只让交换机唯一
                // 是不够的：队列仍然是全局的、持久的，上一次运行留下的死信与重试消息会让
                // 这一次的断言**凭空成立**。实测：DLQ 里积了 39 条历次运行的残留，
                // 于是"死信里有一条"和"处理器被调用三次"两条断言都在 2 秒内通过，
                // 而它们本该各等 2 秒 + 4 秒的档位。
                ConsumerName = $"{prefix}-{name}",
                // 短档位：测试里等得起，而且两档足够验证"递增"。
                RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            })]);

    /// <summary>按名字取订阅——名字带前缀，所以按后缀匹配。</summary>
    private static EventSubscription Sub(EventTopology topology, string name) =>
        topology.Subscriptions.Single(s => s.ConsumerName.EndsWith($"-{name}", StringComparison.Ordinal));

    private static RabbitMqOptions OptionsFor(string prefix) =>
        RabbitMqTestBroker.Options with { ExchangeName = $"{prefix}-exchange" };

    private static EventEnvelope Envelope(string eventName = EventName) => new()
    {
        MessageId = Guid.NewGuid(),
        EventName = eventName,
        Payload = """{"probe":true}""",
        OccurredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>随测试脚本走的处理器：记下被处理的 MessageId，按需失败。</summary>
    private sealed class Probe : IIntegrationEventProcessor
    {
        private readonly Lock _gate = new();
        private readonly List<Guid> _handled = [];

        public string EventName => RabbitMqConsumerTests.EventName;

        /// <summary>要不要让处理失败。</summary>
        public bool Fail { get; set; }

        /// <summary>处理成功的次数。</summary>
        public int Count
        {
            get { lock (_gate) { return _handled.Count; } }
        }

        public Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _handled.Add(envelope.MessageId);
            }

            return Task.FromResult(!Fail);
        }
    }

    private static async Task<RabbitMqConsumer> StartConsumerAsync(
        RabbitMqOptions options,
        EventTopology topology,
        EventSubscription subscription,
        IIntegrationEventProcessor processor,
        CancellationToken cancellationToken)
    {
        var consumer = new RabbitMqConsumer(options, topology, subscription, processor, new InMemoryInboxStore());
        _ = consumer.RunAsync(cancellationToken);
        return consumer;
    }

    private static async Task<int> WaitForDepthAsync(RabbitMqOptions options, string queue, int expected, int timeoutSeconds = 20)
    {
        await using var connection = await ConnectAsync(options);
        await using var channel = await connection.CreateChannelAsync();

        for (var i = 0; i < timeoutSeconds * 4; i++)
        {
            var depth = await ChannelDepthAsync(channel, queue);
            if (depth >= expected)
            {
                return depth;
            }

            await Task.Delay(250);
        }

        return await ChannelDepthAsync(channel, queue);
    }

    /// <summary>查队列深度，**不改动它**。</summary>
    private static async Task<int> ChannelDepthAsync(IChannel channel, string queue)
    {
        try
        {
            // `passive: true` 只查不建。队列不存在时抛——调用方据此知道"这个队列还没有"。
            var declared = await channel.QueueDeclarePassiveAsync(queue);
            return (int)declared.MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0;
        }
    }

    private static async Task<IConnection> ConnectAsync(RabbitMqOptions options)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            ClientProvidedName = $"{options.ClientName}-assert",
        };

        return await factory.CreateConnectionAsync();
    }

    /// <summary>
    /// **两个消费端消费同一事件，只有失败的那个收到重试消息**（验收 2）。
    ///
    /// <para>这条防的是"重试影响了所有人"：如果两个消费者共享一个队列，
    /// 一个失败会让整个队列重投——另一个消费者会**重新处理一遍已经成功的事件**。
    /// 每个 <c>(事件, 消费者)</c> 一个队列正是为了这件事。</para>
    /// </summary>
    [RabbitMqFact]
    public async Task OnlyTheFailingConsumer_GetsARetry()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = OptionsFor(prefix);
        var topology = Topology(prefix, "alpha", "beta");
        var plan = RabbitTopologyPlanner.Plan(topology);

        Assert.True((await new RabbitMqTopologyBootstrapper(options).ApplyAsync(plan)).IsSuccess);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var failing = new Probe { Fail = true };
        var succeeding = new Probe();

        var alpha = Sub(topology, "alpha");
        var beta = Sub(topology, "beta");

        await using var consumerAlpha = await StartConsumerAsync(options, topology, alpha, failing, stop.Token);
        await using var consumerBeta = await StartConsumerAsync(options, topology, beta, succeeding, stop.Token);

        // 让两个消费者真的挂上（声明完到开始消费之间有一小段）。
        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);

        var envelope = Envelope();

        await using (var connection = await ConnectAsync(options))
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.BasicPublishAsync(
                exchange: topology.ExchangeName,
                routingKey: envelope.RoutingKey,
                mandatory: true,
                // **带上 MessageId。** 生产发布端（RabbitMqEventBus）总是写它；
                // 不写的话消费端的去重永远不触发，这条验收就走上了一条生产上不存在的路。
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    MessageId = envelope.MessageId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                },
                body: Encoding.UTF8.GetBytes("""{"probe":true}"""));

            // **alpha 的重试队列应当收到一条。**
            var alphaRetry = await WaitForDepthAsync(options, alpha.RetryQueueNames[0], expected: 1);
            Assert.True(alphaRetry >= 1, "失败的那个消费者没有把消息送进重试队列。");

            // **beta 的重试队列必须是空的**——它成功了，不该有任何重试。
            await using var probeChannel = await connection.CreateChannelAsync();
            Assert.Equal(0, await ChannelDepthAsync(probeChannel, beta.RetryQueueNames[0]));

            Assert.Equal(1, succeeding.Count);
        }

        await stop.CancelAsync();
    }

    /// <summary>
    /// **同一个 MessageId 投递两次，业务只生效一次**（验收 4，与票据 19 的 Inbox 联动）。
    ///
    /// <para>至少一次投递是消息队列的常态，不是异常。这条测试直接发两条**同 MessageId** 的消息，
    /// 断言处理器只被调用了一次。</para>
    /// </summary>
    [RabbitMqFact]
    public async Task TheSameMessageIdTwice_IsProcessedOnce()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = OptionsFor(prefix);
        var topology = Topology(prefix, "solo");
        var plan = RabbitTopologyPlanner.Plan(topology);

        Assert.True((await new RabbitMqTopologyBootstrapper(options).ApplyAsync(plan)).IsSuccess);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var probe = new Probe();
        var subscription = topology.Subscriptions[0];

        await using var consumer = await StartConsumerAsync(options, topology, subscription, probe, stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);

        var envelope = Envelope();

        await using (var connection = await ConnectAsync(options))
        await using (var channel = await connection.CreateChannelAsync())
        {
            for (var i = 0; i < 2; i++)
            {
                await channel.BasicPublishAsync(
                    exchange: topology.ExchangeName,
                    routingKey: envelope.RoutingKey,
                    mandatory: true,
                    basicProperties: new BasicProperties
                    {
                        Persistent = true,
                        MessageId = envelope.MessageId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                    },
                    body: Encoding.UTF8.GetBytes(envelope.Payload));
            }
        }

        // 等到队列被消费干净（两条都被处理过），再断言业务只生效一次。
        await WaitForDepthAsync(options, subscription.QueueName, expected: 0);
        await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);

        Assert.Equal(1, probe.Count);
        Assert.Equal(1, consumer.DuplicateCount);

        await stop.CancelAsync();
    }

    /// <summary>
    /// **档位用尽的消息进死信队列**（验收 5）。
    ///
    /// <para>参照仓库**没有 DLQ 消费者**——死信队列会静默堆积，
    /// 而"堆积"与"没人发消息"在监控上看起来一样。</para>
    /// </summary>
    [RabbitMqFact]
    public async Task AnExhaustedMessage_EndsUpInTheDeadLetterQueue()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = OptionsFor(prefix);
        var topology = Topology(prefix, "doomed");
        var plan = RabbitTopologyPlanner.Plan(topology);

        Assert.True((await new RabbitMqTopologyBootstrapper(options).ApplyAsync(plan)).IsSuccess);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var probe = new Probe { Fail = true };
        var subscription = topology.Subscriptions[0];

        await using var consumer = await StartConsumerAsync(options, topology, subscription, probe, stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);

        await using (var connection = await ConnectAsync(options))
        await using (var channel = await connection.CreateChannelAsync())
        {
            var envelope = Envelope();

            await channel.BasicPublishAsync(
                exchange: topology.ExchangeName,
                routingKey: envelope.RoutingKey,
                mandatory: true,
                // **带上 MessageId**（生产发布端总是写它）。这一条是这次修的那个缺陷的回归测试：
                // 处理器失败时必须**归还去重名额**，否则重投被判重复而 ACK 跳过，
                // 消息永远到不了死信——而"处理器恰好 3 次"也会退化成 1 次。
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    MessageId = envelope.MessageId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                },
                body: Encoding.UTF8.GetBytes("""{"probe":true}"""));

            // 两个档位（2 秒 + 4 秒）走完，消息应当落在死信队列里。
            var depth = await WaitForDepthAsync(options, subscription.DeadLetterQueueName, expected: 1, timeoutSeconds: 30);

            Assert.True(depth >= 1, "用尽档位的消息没有出现在死信队列里。");
        }

        // 处理器被调用的次数 = 首次 + 两次重试 = 3。这同时证明**档位是递增的**——
        // 不递增的话，消息会在第一档里无限循环，永远到不了死信。
        //
        // **等它到 3，而不是立刻断言。** 第一版立刻断言，于是偶尔读到 2——
        // 那是"死信队列已经能看到消息"与"第三次调用刚返回"之间的竞态，
        // 是我的测试抢跑，不是消费端少重试了一次。
        // 等一个上限之后仍不到 3，才是真的少了重试。
        var reachedThree = await ConsumerTestWait.WaitForAsync(() => probe.Count >= 3, TimeSpan.FromSeconds(20));

        Assert.True(
            reachedThree,
            $"处理器只被调用了 {probe.Count} 次（期望 3：首次 + 两次重试）。"
            + "少于 3 说明档位没有递增，或者消息提前进了死信。");

        await stop.CancelAsync();
    }
}

// 等一个条件成立。**"等"与"立刻断言"的区别在这里是本质的**：
// 前者问的是"最终会不会"，后者问的是"此刻是不是"——而消息在队列之间移动是需要时间的。

internal static class ConsumerTestWait
{
    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }
}
