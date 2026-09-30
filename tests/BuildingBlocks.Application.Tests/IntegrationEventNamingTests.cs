using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Tests.RenamedEventNamespace;

namespace NexusStackNext.BuildingBlocks.Application.Tests;

/// <summary>
/// 事件命名规则。<b>这里每一条都是对参照仓库具体缺陷的回归测试。</b>
/// </summary>
public sealed class IntegrationEventNamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RoutingKey_IsTheDeclaredEventName()
    {
        var @event = new UserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now };

        Assert.Equal("identity.user-registered.v1", IntegrationEventNaming.RoutingKey(@event.EventName));
    }

    [Fact]
    public void RoutingKey_DoesNotContainTheClrTypeName()
    {
        var @event = new UserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now };

        var routingKey = IntegrationEventNaming.RoutingKey(@event.EventName);

        Assert.DoesNotContain(nameof(UserRegisteredEvent), routingKey, StringComparison.Ordinal);
        Assert.DoesNotContain("Tests", routingKey, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(UserRegisteredEvent).FullName!, routingKey, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingKey_IsStableWhenNamespaceAndTypeNameChange()
    {
        // 同一个事件，两种类名、两种命名空间 —— 路由键必须一致。
        // 参照仓库做不到这一点：路由键就是 CLR 全名。
        var original = new UserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now };
        var renamed = new LegacyUserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now };

        Assert.NotEqual(typeof(UserRegisteredEvent).FullName, typeof(LegacyUserRegisteredEvent).FullName);

        Assert.Equal(
            IntegrationEventNaming.RoutingKey(original.EventName),
            IntegrationEventNaming.RoutingKey(renamed.EventName));
    }

    [Fact]
    public void ConsumerQueue_IsPerConsumer()
    {
        var auditing = IntegrationEventNaming.ConsumerQueue("identity.user-registered.v1", "auditing");
        var scheduling = IntegrationEventNaming.ConsumerQueue("identity.user-registered.v1", "scheduling");

        Assert.Equal("identity.user-registered.v1.auditing", auditing);
        Assert.NotEqual(auditing, scheduling);
    }

    [Fact]
    public void RetryQueue_DerivesFromConsumerQueue_SoOneConsumerCannotRedeliverToAnother()
    {
        // 参照仓库的缺陷（review/04 发现 1）：重试队列的 DLX 按**事件名**回主交换机，
        // 而主交换机上每个处理器各有一个以该事件名绑定的队列 —— 一个处理器失败，
        // 同事件的所有处理器都会重收一遍。从消费队列派生，重试只会回到该消费端自己。
        const string eventName = "identity.user-registered.v1";
        var auditingQueue = IntegrationEventNaming.ConsumerQueue(eventName, "auditing");
        var schedulingQueue = IntegrationEventNaming.ConsumerQueue(eventName, "scheduling");

        var auditingRetry = IntegrationEventNaming.RetryQueue(auditingQueue, "5s");
        var schedulingRetry = IntegrationEventNaming.RetryQueue(schedulingQueue, "5s");

        Assert.NotEqual(auditingRetry, schedulingRetry);
        Assert.StartsWith(auditingQueue, auditingRetry, StringComparison.Ordinal);
        Assert.StartsWith(schedulingQueue, schedulingRetry, StringComparison.Ordinal);
        Assert.Equal("identity.user-registered.v1.auditing.retry.5s", auditingRetry);
    }

    [Fact]
    public void DeadLetterQueue_IsPerConsumer()
    {
        var auditingQueue = IntegrationEventNaming.ConsumerQueue("identity.user-registered.v1", "auditing");
        var schedulingQueue = IntegrationEventNaming.ConsumerQueue("identity.user-registered.v1", "scheduling");

        Assert.NotEqual(
            IntegrationEventNaming.DeadLetterQueue(auditingQueue),
            IntegrationEventNaming.DeadLetterQueue(schedulingQueue));
        Assert.Equal("identity.user-registered.v1.auditing.dead", IntegrationEventNaming.DeadLetterQueue(auditingQueue));
    }

    [Fact]
    public void Naming_RejectsEmptyInput()
    {
        // 用 ThrowsAny：null 走的是 ArgumentNullException，空白走的是 ArgumentException，
        // 两者都是调用方的错，不必把断言绑死在具体子类上。
        Assert.ThrowsAny<ArgumentException>(() => IntegrationEventNaming.RoutingKey(" "));
        Assert.ThrowsAny<ArgumentException>(() => IntegrationEventNaming.RoutingKey(null!));
        Assert.ThrowsAny<ArgumentException>(() => IntegrationEventNaming.ConsumerQueue("e", string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => IntegrationEventNaming.RetryQueue(string.Empty, "5s"));
        Assert.ThrowsAny<ArgumentException>(() => IntegrationEventNaming.DeadLetterQueue(null!));
    }

    [Fact]
    public void EventId_IsGeneratedPerInstance_ButCanBePinned()
    {
        var pinned = Guid.NewGuid();
        var a = new UserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now, EventId = pinned };
        var b = new UserRegisteredEvent(Guid.NewGuid()) { OccurredAt = Now };

        Assert.Equal(pinned, a.EventId);
        Assert.NotEqual(Guid.Empty, b.EventId);
        Assert.NotEqual(a.EventId, b.EventId);
    }
}
