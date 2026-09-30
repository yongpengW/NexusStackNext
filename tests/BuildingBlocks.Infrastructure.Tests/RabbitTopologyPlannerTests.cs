using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// 拓扑规划。<b>核心回归 review/04 发现 1</b>：重试必须只回到失败的那个消费端，
/// 而不是让同事件的所有消费端重收一遍。
/// </summary>
public sealed class RabbitTopologyPlannerTests
{
    private const string EventName = "identity.user-registered.v1";
    private const string Consumer = "auditing";
    private const string ConsumerQueue = "identity.user-registered.v1.auditing";

    private static EventSubscription Subscription(
        string consumer = Consumer,
        string eventName = EventName,
        params TimeSpan[] retryDelays) => new()
    {
        EventName = eventName,
        ConsumerName = consumer,
        RetryDelays = retryDelays,
    };

    private static TopologyPlan PlanFor(params EventSubscription[] subscriptions) =>
        RabbitTopologyPlanner.Plan(EventTopology.Create("nexusstack.events", subscriptions));

    [Fact]
    public void ConsumerQueue_IsBoundByEventName()
    {
        var plan = PlanFor(Subscription(retryDelays: [TimeSpan.FromSeconds(5)]));

        var binding = Assert.Single(plan.Bindings);
        Assert.Equal(ConsumerQueue, binding.Queue);
        Assert.Equal(EventName, binding.RoutingKey);
    }

    [Fact]
    public void EachTierGetsItsOwnDurableQueue_WithThatTierTtl()
    {
        var plan = PlanFor(Subscription(retryDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)]));

        var first = plan.FindQueue($"{ConsumerQueue}.retry.5s");
        var second = plan.FindQueue($"{ConsumerQueue}.retry.60s");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Durable);
        Assert.Equal(5000, first.Arguments[RabbitTopologyPlanner.MessageTtlArgument]);
        Assert.Equal(60000, second.Arguments[RabbitTopologyPlanner.MessageTtlArgument]);
    }

    [Fact]
    public void RetryQueue_DeadLettersBackToItsOwnConsumerQueue_NotToTheEventName()
    {
        // 参照仓库 EventSubscriber.cs:255 用**事件名**当死信路由键，
        // 而主交换机上每个处理器各有一个以该事件名绑定的队列 ⇒ 一个失败，全体重收。
        var plan = PlanFor(Subscription(retryDelays: [TimeSpan.FromSeconds(5)]));

        var retryQueue = plan.FindQueue($"{ConsumerQueue}.retry.5s")!;
        var deadLetterRoutingKey = retryQueue.Arguments[RabbitTopologyPlanner.DeadLetterRoutingKeyArgument];

        Assert.Equal(ConsumerQueue, deadLetterRoutingKey);
        Assert.NotEqual(EventName, deadLetterRoutingKey);

        // 走默认交换机：AMQP 规定它按队列名精确投递，只会到达这一个队列。
        Assert.Equal(RabbitTopologyPlanner.DefaultExchange, retryQueue.Arguments[RabbitTopologyPlanner.DeadLetterExchangeArgument]);
    }

    [Fact]
    public void TwoConsumersOfTheSameEvent_NeverShareARetryQueue()
    {
        var plan = PlanFor(
            Subscription("auditing", EventName, TimeSpan.FromSeconds(5)),
            Subscription("scheduling", EventName, TimeSpan.FromSeconds(5)));

        var auditingRetry = plan.FindQueue("identity.user-registered.v1.auditing.retry.5s")!;
        var schedulingRetry = plan.FindQueue("identity.user-registered.v1.scheduling.retry.5s")!;

        Assert.NotEqual(auditingRetry.Name, schedulingRetry.Name);
        Assert.Equal(
            "identity.user-registered.v1.auditing",
            auditingRetry.Arguments[RabbitTopologyPlanner.DeadLetterRoutingKeyArgument]);
        Assert.Equal(
            "identity.user-registered.v1.scheduling",
            schedulingRetry.Arguments[RabbitTopologyPlanner.DeadLetterRoutingKeyArgument]);
    }

    [Fact]
    public void EveryConsumerGetsADeadLetterQueue()
    {
        var plan = PlanFor(
            Subscription("auditing", EventName, TimeSpan.FromSeconds(5)),
            Subscription("scheduling", "identity.user-disabled.v1"));

        Assert.NotNull(plan.FindQueue("identity.user-registered.v1.auditing.dead"));
        Assert.NotNull(plan.FindQueue("identity.user-disabled.v1.scheduling.dead"));
    }

    [Fact]
    public void PlanDeclaresNoQueueOutsideTheTopologyDeclaration()
    {
        // 拓扑必须集中声明，不能有隐式队列冒出来。
        var topology = EventTopology.Create(
            "nexusstack.events",
            [Subscription(retryDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)])]);

        var plan = RabbitTopologyPlanner.Plan(topology);

        Assert.Equal(topology.AllQueueNames, plan.Queues.Select(static queue => queue.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void QueueNamesAreUnique()
    {
        var plan = PlanFor(
            Subscription("auditing", EventName, TimeSpan.FromSeconds(5)),
            Subscription("scheduling", EventName, TimeSpan.FromSeconds(5)));

        var names = plan.Queues.Select(static queue => queue.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExchangeIsDeclaredAsTopic()
    {
        var plan = PlanFor(Subscription());

        Assert.Equal("nexusstack.events", plan.ExchangeName);
        Assert.Equal("topic", plan.ExchangeType);
    }

    [Fact]
    public void Plan_RejectsNullTopology()
    {
        Assert.Throws<ArgumentNullException>(() => RabbitTopologyPlanner.Plan(null!));
    }
}
