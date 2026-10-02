using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using RabbitMQ.Client;

namespace NexusStackNext.RabbitMq.IntegrationTests;

/// <summary>
/// **通道断了之后消费者要自己回来**（票据 21 验收 3）。
///
/// <para>参照仓库没有任何通道级恢复处理。那种失效最安静：broker 抖一下，
/// 消费者就永远不再消费了，而**进程还活着、健康检查还是绿的**——
/// 所有能看的地方都正常，只有"消息不再被处理"这一件事在发生。</para>
///
/// <para>这条测试从 **broker 侧**掐断连接（管理 API），而不是调用某个测试钩子：
/// 被验证的是"broker 断我，我自己回来"，那才是生产里会发生的事。</para>
/// </summary>
public sealed class RabbitMqConsumerRecoveryTests
{
    private const string EventName = "identity.user.registered";

    private sealed class CountingProcessor : IIntegrationEventProcessor
    {
        private int _count;

        public string EventName => RabbitMqConsumerRecoveryTests.EventName;

        public int Count => Volatile.Read(ref _count);

        public Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// 掐断这个消费者的连接，**并且只掐它**。
    ///
    /// <para>broker 是共享的——它上面跑着别的服务。所以匹配必须精确：
    /// "把所有连接都杀掉"能通过测试，代价是顺手断掉别人的生产流量。</para>
    ///
    /// <para>找不到时返回它**实际看到的那些名字**：只报"没找到"的话，
    /// 失败信息里没有诊断线索，而这条测试的本意正是"别静默空转"。</para>
    /// </summary>
    private static async Task<(bool Killed, IReadOnlyList<string> Observed)> KillConnectionsAsync(
        RabbitMqOptions options,
        string consumerName)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{options.HostName}:15672") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}")));

        // 连接在管理 API 里出现有个几秒的延迟，轮询等它。
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var list = await http.GetStringAsync(new Uri("/api/connections", UriKind.Relative));
            using var document = JsonDocument.Parse(list);

            var observed = new List<string>();
            string? match = null;

            foreach (var connection in document.RootElement.EnumerateArray())
            {
                var name = connection.GetProperty("client_properties").TryGetProperty("connection_name", out var value)
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

                observed.Add(name.Length == 0 ? "(无名)" : name);

                // 客户端名形如 `{ClientName}-{ConsumerName}`，用**包含**匹配：
                // broker 会在某些情况下改写或截断它。
                if (name.Contains(consumerName, StringComparison.Ordinal))
                {
                    match = connection.GetProperty("name").GetString();
                }
            }

            if (match is not null)
            {
                using var response = await http.DeleteAsync(
                    new Uri($"/api/connections/{Uri.EscapeDataString(match)}", UriKind.Relative));

                if (response.IsSuccessStatusCode)
                {
                    return (true, observed);
                }
            }

            await Task.Delay(500);
        }

        // 最后再取一次，供失败信息使用。
        var final = await http.GetStringAsync(new Uri("/api/connections", UriKind.Relative));
        using var finalDocument = JsonDocument.Parse(final);

        return (false, [.. finalDocument.RootElement.EnumerateArray().Select(connection =>
            connection.GetProperty("client_properties").TryGetProperty("connection_name", out var value)
                ? value.GetString() ?? "(无名)"
                : "(无名)")]);
    }

    /// <summary>
    /// 通道断了之后消费者**自己重建并继续消费**。
    /// </summary>
    [RabbitMqFact]
    public async Task AfterTheConnectionIsKilled_TheConsumerRecovers_AndKeepsConsuming()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = RabbitMqTestBroker.Options with { ExchangeName = $"{prefix}-exchange" };

        var topology = EventTopology.Create(
            options.ExchangeName,
            [
                new EventSubscription
                {
                    EventName = EventName,
                    // 带前缀的理由同 RabbitMqConsumerTests：队列名只由 (事件名, 消费端名) 派生，
                    // 不带前缀就是一条跨运行共享的持久队列。
                    ConsumerName = $"{prefix}-recovering",
                    RetryDelays = [TimeSpan.FromSeconds(2)],
                },
            ]);

        Assert.True((await new RabbitMqTopologyBootstrapper(options).ApplyAsync(RabbitTopologyPlanner.Plan(topology), default)).IsSuccess);

        var subscription = topology.Subscriptions[0];
        var processor = new CountingProcessor();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var consumer = new RabbitMqConsumer(
            options, topology, subscription, processor);

        _ = consumer.RunAsync(stop.Token);

        // 一、先是正常的：发一条，被处理。
        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
        await PublishAsync(options, topology, stop.Token);

        var firstHandled = await WaitForAsync(() => processor.Count >= 1, TimeSpan.FromSeconds(15), stop.Token);
        Assert.True(firstHandled, "第一条消息都没被处理——后面的恢复断言就没有意义了。");

        // 二、**从 broker 侧掐断它的连接。**
        var (killed, observed) = await KillConnectionsAsync(options, subscription.ConsumerName);

        Assert.True(
            killed,
            "管理 API 没杀掉任何连接——这条测试就成了空转（而空转的测试与通过的测试长得一样）。"
            + $"当时 broker 上的连接名：{string.Join("、", observed)}");

        // 三、消费者应当自己重建（RecoveryCount 增加）。
        var recovered = await WaitForAsync(() => consumer.RecoveryCount >= 1, TimeSpan.FromSeconds(20), stop.Token);
        Assert.True(recovered, "通道断了之后消费者没有重建。");

        // 四、**恢复之后还得真的能消费。** 只验"重建了"是不够的——
        //     一个每次断线都重建、但重建后挂不上的实现同样能让上一步通过。
        var before = processor.Count;
        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);

        var second = await WaitForAsync(
            async () =>
            {
                await PublishAsync(options, topology, stop.Token);
                return await WaitForAsync(() => processor.Count > before, TimeSpan.FromSeconds(8), stop.Token);
            },
            TimeSpan.FromSeconds(40),
            stop.Token);

        Assert.True(second, "消费者重建了，但恢复之后没能继续消费。");

        await stop.CancelAsync();
    }

    private static async Task PublishAsync(RabbitMqOptions options, EventTopology topology, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(options);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        var envelope = new EventEnvelope
        {
            MessageId = Guid.NewGuid(),
            EventName = EventName,
            Payload = """{"probe":true}""",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        await channel.BasicPublishAsync(
            exchange: topology.ExchangeName,
            routingKey: envelope.RoutingKey,
            mandatory: true,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = envelope.MessageId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            },
            body: Encoding.UTF8.GetBytes(envelope.Payload),
            cancellationToken: cancellationToken);
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
            ClientProvidedName = $"{options.ClientName}-publisher",
        };

        return await factory.CreateConnectionAsync();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return condition();
    }

    private static async Task<bool> WaitForAsync(
        Func<Task<bool>> attempt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await attempt())
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }
}
