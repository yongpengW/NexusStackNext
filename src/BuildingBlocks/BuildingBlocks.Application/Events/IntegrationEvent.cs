namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// 集成事件契约基类（ADR-0007）。
/// <para>
/// <b><see cref="EventName"/> 必须显式声明、带版本号</b>，形如 <c>identity.user-registered.v1</c>。
/// 路由键、队列名、幂等键全部基于它，<b>绝不</b>基于 CLR 类型名或命名空间。
/// </para>
/// <para>
/// 为什么这条是硬性要求：参照仓库用 CLR 类型全名当路由键（<c>EventSubscriber.cs:196/255/294-297</c>），
/// 且事件注册表靠扫描<b>消费端</b>的处理器来发现。这意味着一次纯粹的重构——把事件类挪到另一个程序集、
/// 或改一个命名空间——就会静默切断消息链路：发布端与消费端各自算出一个不同的路由键，
/// 没有编译错误，也没有启动失败，消息只是不再到达。而"按上下文拆程序集"正是本项目的第一个动作。
/// </para>
/// <para>
/// 领域事件（<c>IDomainEvent</c>）与集成事件不是一回事：前者是进程内的事实，
/// 后者是跨上下文、契约化、有版本承诺的消息。二者的转换发生在应用层。
/// </para>
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>
    /// 事件实例标识，消费端据此去重（Inbox）。默认生成，测试里可覆盖为确定值。
    /// </summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// 发生时刻（UTC）。<b>必须由调用方用 <c>IClock</c> 赋值</b>，事件自己不取时间——
    /// 否则事件的发生时刻无法在测试中确定。
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// 稳定、显式、带版本的事件名。推荐格式 <c>&lt;上下文&gt;.&lt;事件&gt;.v&lt;主版本&gt;</c>。
    /// </summary>
    public abstract string EventName { get; }
}

/// <summary>
/// 集成事件的命名规则。集中在<b>一处</b>，避免拓扑靠扫描消费端推导。
/// </summary>
public static class IntegrationEventNaming
{
    private const char Separator = '.';

    /// <summary>交换机上的路由键：就是事件名本身。</summary>
    /// <param name="eventName">事件名。</param>
    /// <returns>路由键。</returns>
    public static string RoutingKey(string eventName) => RequireEventName(eventName);

    /// <summary>某个消费端的队列名：<c>{事件名}.{消费端}</c>。</summary>
    /// <param name="eventName">事件名。</param>
    /// <param name="consumerName">消费端名称，例如 <c>auditing</c>。</param>
    /// <returns>队列名。</returns>
    public static string ConsumerQueue(string eventName, string consumerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        return $"{RequireEventName(eventName)}{Separator}{consumerName}";
    }

    /// <summary>
    /// 重试队列名：<b>从消费队列派生，而不是从事件名派生</b>。
    /// <para>
    /// 这条不是风格问题。参照仓库的重试队列 DLX 按**事件名**回主交换机
    /// （<c>EventSubscriber.cs:255</c>），而主交换机上每个处理器各有一个以该事件名绑定的队列
    /// （<c>:196</c>、<c>:294-297</c>），于是一个处理器失败会让<b>同事件的所有处理器</b>重收一遍。
    /// 从消费队列派生，重试就只会回到该消费端自己的队列。
    /// </para>
    /// </summary>
    /// <param name="consumerQueue">消费队列名。</param>
    /// <param name="tier">延迟档位标识，例如 <c>5s</c>。</param>
    /// <returns>重试队列名。</returns>
    public static string RetryQueue(string consumerQueue, string tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);
        return $"{consumerQueue}{Separator}retry{Separator}{tier}";
    }

    /// <summary>死信队列名：同样从消费队列派生。</summary>
    /// <param name="consumerQueue">消费队列名。</param>
    /// <returns>死信队列名。</returns>
    public static string DeadLetterQueue(string consumerQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerQueue);
        return $"{consumerQueue}{Separator}dead";
    }

    private static string RequireEventName(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return eventName;
    }
}
