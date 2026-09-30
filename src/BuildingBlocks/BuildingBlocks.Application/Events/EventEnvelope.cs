using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// 投递到总线上的消息封套。
/// <para>
/// Outbox 里存的就是它的字段。<b>刻意不带 CLR 类型</b>——投递端只需要"事件名 + 载荷"，
/// 不需要把事件反序列化回对象再序列化一次。这也让"改命名空间/换程序集不影响路由"
/// （ADR-0007）成为结构上的必然：链路上根本没有任何地方见过类型名。
/// </para>
/// <para>
/// 它住在 Application 而不是 Infrastructure：它是<b>端口两侧共用的契约</b>，
/// 把契约放在实现程序集里，会让任何要用契约的人都必须依赖实现（见票据 45）。
/// </para>
/// </summary>
public sealed record EventEnvelope
{
    /// <summary>消息标识，消费端据此去重。</summary>
    public required Guid MessageId { get; init; }

    /// <summary>稳定、带版本的事件名。</summary>
    public required string EventName { get; init; }

    /// <summary>已序列化的载荷。</summary>
    public required string Payload { get; init; }

    /// <summary>事件发生时刻（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>交换机路由键：就是事件名本身。</summary>
    public string RoutingKey => IntegrationEventNaming.RoutingKey(EventName);
}

/// <summary>消息层的标准失败。</summary>
public static class MessagingErrors
{
    /// <summary>消息无法路由到任何队列——<b>必须当成失败</b>，不能记日志后当作成功。</summary>
    /// <param name="eventName">事件名。</param>
    /// <returns>错误。</returns>
    public static Error Unroutable(string eventName) =>
        new("messaging.unroutable", $"消息无法路由到任何队列：{eventName}。");

    /// <summary>broker 不可用。</summary>
    /// <param name="reason">底层原因。</param>
    /// <returns>错误。</returns>
    public static Error BrokerUnavailable(string reason) =>
        new("messaging.broker_unavailable", reason);

    /// <summary>broker 明确拒绝。</summary>
    /// <param name="reason">底层原因。</param>
    /// <returns>错误。</returns>
    public static Error Rejected(string reason) =>
        new("messaging.rejected", reason);
}
