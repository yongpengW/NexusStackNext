using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// 拓扑声明。<b>重点回归 review/04 发现 1</b>：重试队列必须从消费队列派生，
/// 否则一个处理器失败会让同事件的所有处理器重收。
/// </summary>
public sealed class EventTopologyTests
{
    private const string EventName = "identity.user-registered.v1";

    private static EventSubscription Subscription(
        string consumer = "auditing",
        string eventName = EventName,
        params TimeSpan[] retryDelays) => new()
        {
            EventName = eventName,
            ConsumerName = consumer,
            RetryDelays = retryDelays,
        };

    [Fact]
    public void QueueNames_DeriveFromEventNameAndConsumer()
    {
        var topology = EventTopology.Create("nexusstack.events", [Subscription()]);

        Assert.Equal("identity.user-registered.v1.auditing", topology.Subscriptions[0].QueueName);
        Assert.Equal("nexusstack.events", topology.ExchangeName);
    }

    [Fact]
    public void RetryQueues_DifferPerConsumer_EvenForTheSameEvent()
    {
        // review/04 发现 1：参照仓库重试队列的 DLX 按**事件名**回主交换机，
        // 而主交换机上每个处理器各有一个以该事件名绑定的队列 —— 一个处理器失败，
        // 同事件的所有处理器都会重收一遍。
        var delay = TimeSpan.FromSeconds(5);
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription("auditing", EventName, delay), Subscription("scheduling", EventName, delay)]);

        var auditing = topology.Subscriptions.Single(s => s.ConsumerName == "auditing");
        var scheduling = topology.Subscriptions.Single(s => s.ConsumerName == "scheduling");

        Assert.Equal("identity.user-registered.v1.auditing.retry.5s", auditing.RetryQueueNames[0]);
        Assert.Equal("identity.user-registered.v1.scheduling.retry.5s", scheduling.RetryQueueNames[0]);
        Assert.Empty(auditing.RetryQueueNames.Intersect(scheduling.RetryQueueNames, StringComparer.Ordinal));
        Assert.NotEqual(auditing.DeadLetterQueueName, scheduling.DeadLetterQueueName);
    }

    [Fact]
    public void RetryQueues_OnePerTier()
    {
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription(retryDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)])]);

        Assert.Equal(
            [
                "identity.user-registered.v1.auditing.retry.5s",
                "identity.user-registered.v1.auditing.retry.60s",
                "identity.user-registered.v1.auditing.retry.300s",
            ],
            topology.Subscriptions[0].RetryQueueNames);
    }

    [Fact]
    public void AllQueueNames_CoversConsumerRetryAndDeadLetter()
    {
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription(retryDelays: [TimeSpan.FromSeconds(5)])]);

        Assert.Equal(
            [
                "identity.user-registered.v1.auditing",
                "identity.user-registered.v1.auditing.dead",
                "identity.user-registered.v1.auditing.retry.5s",
            ],
            topology.AllQueueNames);
    }

    [Fact]
    public void Bindings_MapEachConsumerQueueToTheEventRoutingKey()
    {
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription("auditing"), Subscription("scheduling")]);

        Assert.All(topology.Bindings, binding =>
            Assert.Equal("identity.user-registered.v1", binding.RoutingKey));
        Assert.Equal(2, topology.Bindings.Count);
    }

    [Fact]
    public void Create_RejectsDuplicateSubscription()
    {
        var exception = Assert.Throws<ArgumentException>(() => EventTopology.Create(
            "nexusstack.events",
            [Subscription(), Subscription()]));

        Assert.Contains("不能声明两次", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsEmptySubscriptions()
    {
        Assert.Throws<ArgumentException>(() => EventTopology.Create("nexusstack.events", []));
    }

    [Fact]
    public void Create_RejectsEmptyExchangeName()
    {
        Assert.Throws<ArgumentException>(() => EventTopology.Create("  ", [Subscription()]));
    }

    [Fact]
    public void Create_RejectsSubSecondRetryTier()
    {
        // 档位名按整秒生成，亚秒档位会退化成 "0s" 并互相撞名。
        Assert.Throws<ArgumentException>(() => EventTopology.Create(
            "nexusstack.events",
            [Subscription(retryDelays: [TimeSpan.FromMilliseconds(500)])]));
    }

    [Fact]
    public void Create_RejectsDuplicateRetryTiers()
    {
        Assert.Throws<ArgumentException>(() => EventTopology.Create(
            "nexusstack.events",
            [Subscription(retryDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)])]));
    }

    [Fact]
    public void DifferentEvents_SameConsumer_GetDistinctQueues()
    {
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription(eventName: "identity.user-registered.v1"), Subscription(eventName: "identity.user-disabled.v1")]);

        // 每个订阅贡献两个名字：消费队列 + 死信队列（这里没有声明重试档位）。
        Assert.Equal(4, topology.AllQueueNames.Count);
        Assert.Contains("identity.user-registered.v1.auditing", topology.AllQueueNames);
        Assert.Contains("identity.user-disabled.v1.auditing", topology.AllQueueNames);
    }
}
