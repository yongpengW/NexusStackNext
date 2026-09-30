using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>一个待声明的队列。参数直接透传给 broker，规划阶段不做任何 I/O。</summary>
/// <param name="Name">队列名。</param>
/// <param name="Durable">是否持久化。</param>
/// <param name="Arguments">队列参数（TTL、死信路由等）。</param>
public sealed record QueuePlan(string Name, bool Durable, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>一条交换机到队列的绑定。</summary>
/// <param name="Queue">队列名。</param>
/// <param name="RoutingKey">路由键。</param>
public sealed record BindingPlan(string Queue, string RoutingKey);

/// <summary>
/// 由 <see cref="EventTopology"/> 编译出的可执行拓扑。
/// <para>规划是<b>纯函数</b>：给定同一份声明，永远得到同一份队列与绑定清单，不依赖 broker 状态，
/// 也不做任何 I/O。因此"重试回程会不会串到别的消费端"这类问题可以完全离线验证。</para>
/// </summary>
public sealed class TopologyPlan
{
    /// <summary>构造拓扑计划。</summary>
    /// <param name="exchangeName">交换机名。</param>
    /// <param name="exchangeType">交换机类型。</param>
    /// <param name="queues">队列清单。</param>
    /// <param name="bindings">绑定清单。</param>
    public TopologyPlan(
        string exchangeName,
        string exchangeType,
        IReadOnlyList<QueuePlan> queues,
        IReadOnlyList<BindingPlan> bindings)
    {
        ExchangeName = exchangeName;
        ExchangeType = exchangeType;
        Queues = queues;
        Bindings = bindings;
    }

    /// <summary>交换机名。</summary>
    public string ExchangeName { get; }

    /// <summary>交换机类型。</summary>
    public string ExchangeType { get; }

    /// <summary>全部队列。</summary>
    public IReadOnlyList<QueuePlan> Queues { get; }

    /// <summary>全部绑定。</summary>
    public IReadOnlyList<BindingPlan> Bindings { get; }

    /// <summary>按名字找队列。</summary>
    /// <param name="name">队列名。</param>
    /// <returns>队列计划；不存在时为 <c>null</c>。</returns>
    public QueuePlan? FindQueue(string name) => Queues.FirstOrDefault(queue => queue.Name == name);
}

/// <summary>
/// 把事件拓扑编译成 RabbitMQ 的队列与绑定。
/// <para>
/// <b>这里是 review/04 发现 1 的修复点。</b>参照仓库的重试队列把死信寄回主交换机，
/// 路由键用的是<b>事件名</b>（<c>EventSubscriber.cs:255</c>），而主交换机上每个处理器各有一个
/// 以该事件名绑定的队列（<c>:196</c>、<c>:294-297</c>）——于是一个处理器失败，
/// 同事件的<b>所有</b>处理器都会重收一遍，重复执行的代价直接被放大成 N 倍。
/// </para>
/// <para>
/// 这里的做法是：重试队列的 <c>x-dead-letter-exchange</c> 指向<b>默认交换机</b>（空字符串），
/// <c>x-dead-letter-routing-key</c> 用<b>该消费端自己的队列名</b>。AMQP 规定默认交换机按队列名
/// 精确投递，因此重试只会回到它自己那一个队列。
/// </para>
/// </summary>
public static class RabbitTopologyPlanner
{
    /// <summary>消息 TTL 参数。</summary>
    public const string MessageTtlArgument = "x-message-ttl";

    /// <summary>死信交换机参数。</summary>
    public const string DeadLetterExchangeArgument = "x-dead-letter-exchange";

    /// <summary>死信路由键参数。</summary>
    public const string DeadLetterRoutingKeyArgument = "x-dead-letter-routing-key";

    /// <summary>默认交换机名。AMQP 规定它按队列名精确投递。</summary>
    public const string DefaultExchange = "";

    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>();

    /// <summary>规划拓扑。</summary>
    /// <param name="topology">事件拓扑声明。</param>
    /// <returns>可执行的队列与绑定清单。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> 为 <c>null</c>。</exception>
    public static TopologyPlan Plan(EventTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var queues = new List<QueuePlan>();
        var bindings = new List<BindingPlan>();

        foreach (var subscription in topology.Subscriptions)
        {
            // 消费队列：按事件名绑定，接收正常投递。
            queues.Add(new QueuePlan(subscription.QueueName, Durable: true, NoArguments));
            bindings.Add(new BindingPlan(
                subscription.QueueName,
                IntegrationEventNaming.RoutingKey(subscription.EventName)));

            // 每档位一个独立的重试队列：靠 TTL 到期 + 死信寄回本消费端。
            for (var tier = 0; tier < subscription.RetryDelays.Count; tier++)
            {
                queues.Add(new QueuePlan(
                    subscription.RetryQueueNames[tier],
                    Durable: true,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [MessageTtlArgument] = (int)subscription.RetryDelays[tier].TotalMilliseconds,
                        [DeadLetterExchangeArgument] = DefaultExchange,
                        [DeadLetterRoutingKeyArgument] = subscription.QueueName,
                    }));
            }

            queues.Add(new QueuePlan(subscription.DeadLetterQueueName, Durable: true, NoArguments));
        }

        return new TopologyPlan(topology.ExchangeName, "topic", queues, bindings);
    }
}
