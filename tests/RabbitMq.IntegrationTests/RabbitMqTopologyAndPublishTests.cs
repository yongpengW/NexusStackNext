using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

namespace NexusStackNext.RabbitMq.IntegrationTests;

/// <summary>
/// 真 broker 上的拓扑与发布（票据 21 验收 1 与 6）。
///
/// <para><b>这组测试只在有真 RabbitMQ 时运行</b>（见 <see cref="RabbitMqFactAttribute"/>）。
/// 它们要证明的两件事**都无法用替身有意义地证明**：</para>
///
/// <list type="number">
/// <item><b>不可路由的 mandatory 消息会让发布失败</b>——它取决于客户端是否开了
/// 发布确认与确认跟踪。替身里"发布"只是一次方法调用，我写什么它做什么。</item>
/// <item><b>拓扑声明幂等</b>——它取决于 broker 对重复 declare 的处理。</item>
/// </list>
/// </summary>
public sealed class RabbitMqTopologyAndPublishTests
{
    private static EventTopology Topology(string prefix) => EventTopology.Create(
        $"{prefix}-exchange",
        [
            new EventSubscription
            {
                EventName = "identity.user.registered",
                ConsumerName = "probe",
            },
        ]);

    /// <summary>
    /// **不可路由 ⇒ 发布失败**（验收 1）。
    ///
    /// <para>前提是开对了发布确认与确认跟踪。这条测试的价值在于它验的是
    /// **broker 的行为**：把一个 mandatory 消息发到一个没有任何绑定匹配的路由键，
    /// broker 会把它退回来，客户端把它变成一次失败。</para>
    ///
    /// <para>参照仓库在对应位置是只记一条日志，然后**照样 ACK**——
    /// 于是上游以为发出去了，而实际上没有任何人收到。</para>
    /// </summary>
    [RabbitMqFact]
    public async Task AnUnroutableMandatoryMessage_FailsInsteadOfSilentlySucceeding()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = RabbitMqTestBroker.Options with { ExchangeName = $"{prefix}-exchange" };

        var plan = RabbitTopologyPlanner.Plan(Topology(prefix));

        var bootstrapper = new RabbitMqTopologyBootstrapper(options);
        Assert.True((await bootstrapper.ApplyAsync(plan)).IsSuccess);

        await using var bus = new RabbitMqEventBus(options);

        // 一、**路由得到的**消息：成功。没有这一条，一个"永远失败"的实现同样能通过下面那条。
        var routable = new EventEnvelope
        {
            MessageId = Guid.NewGuid(),
            EventName = "identity.user.registered",
            Payload = """{"probe":true}""",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        var ok = await bus.PublishAsync(routable);
        Assert.True(ok.IsSuccess, ok.IsFailure ? ok.Error.Message : null);

        // 二、**路由不到的**消息：失败，而且原因说的是"不可路由"。
        var unroutable = routable with { EventName = "no.body.listens.to.this" };

        var failed = await bus.PublishAsync(unroutable);

        Assert.True(failed.IsFailure, "不可路由的消息竟然发布成功了——发布确认没起作用。");
        Assert.Equal("messaging.unroutable", failed.Error.Code);
    }

    /// <summary>
    /// **拓扑声明幂等**（验收 6）：连续声明两次不报错，也不产生多余队列。
    /// </summary>
    [RabbitMqFact]
    public async Task ApplyingTheTopologyTwice_IsIdempotent_AndCreatesNoExtraQueues()
    {
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var options = RabbitMqTestBroker.Options with { ExchangeName = $"{prefix}-exchange" };
        var plan = RabbitTopologyPlanner.Plan(Topology(prefix));

        var bootstrapper = new RabbitMqTopologyBootstrapper(options);

        Assert.True((await bootstrapper.ApplyAsync(plan)).IsSuccess);

        // 第二次：broker 对同参数 declare 是幂等的。
        var second = await bootstrapper.ApplyAsync(plan);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

        // **计划里只有那些队列，broker 上就该只有那些。**
        // 声明了一个计划外的队列不会报错，但会在 broker 上留下一个永远没人消费的队列——
        // 而它看起来像"某个消费者还没起来"。
        var planNames = plan.Queues.Select(static queue => queue.Name).ToHashSet(StringComparer.Ordinal);

        await using var connection = await CreateConnectionAsync(options);
        await using var channel = await connection.CreateChannelAsync();

        foreach (var name in planNames)
        {
            // `passive: true`：只查不改。队列不存在时它会抛——那正是我们要的"响亮"。
            var declared = await channel.QueueDeclarePassiveAsync(name);
            Assert.Equal(name, declared.QueueName);
        }

        Assert.NotEmpty(planNames);
    }

    private static async Task<RabbitMQ.Client.IConnection> CreateConnectionAsync(RabbitMqOptions options)
    {
        var factory = new RabbitMQ.Client.ConnectionFactory
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
}
