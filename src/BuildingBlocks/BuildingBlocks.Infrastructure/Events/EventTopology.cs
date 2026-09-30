using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 一个消费端对某个事件的订阅。
/// <para>队列名、重试队列名、死信队列名全部由 <see cref="IntegrationEventNaming"/> 派生，
/// 不在这里手写字符串——手写就会出现"某个地方名字对不上"的隐性故障。</para>
/// </summary>
public sealed record EventSubscription
{
    /// <summary>事件名。</summary>
    public required string EventName { get; init; }

    /// <summary>消费端名称，例如 <c>auditing</c>。</summary>
    public required string ConsumerName { get; init; }

    /// <summary>
    /// 各延迟档位。<b>每档位一个独立的重试队列</b>（保留参照仓库这一设计，它是好设计）。
    /// 必须为整秒且互不相同。
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [];

    /// <summary>该消费端的队列名。</summary>
    public string QueueName => IntegrationEventNaming.ConsumerQueue(EventName, ConsumerName);

    /// <summary>各档位的重试队列名，<b>从消费队列派生</b>。</summary>
    public IReadOnlyList<string> RetryQueueNames =>
        [.. RetryDelays.Select(delay => IntegrationEventNaming.RetryQueue(QueueName, TierName(delay)))];

    /// <summary>该消费端的死信队列名。</summary>
    public string DeadLetterQueueName => IntegrationEventNaming.DeadLetterQueue(QueueName);

    internal static string TierName(TimeSpan delay) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)delay.TotalSeconds}s");
}

/// <summary>
/// 事件的拓扑声明：交换机 + 全部订阅。
/// <para>
/// <b>集中声明，不从消费端处理器扫描推导。</b>参照仓库的事件注册表是扫描消费者类型得到的
/// （<c>EventSubscriber.cs:196</c>、<c>:294-297</c>），于是拓扑成了"代码布局的副产物"，
/// 谁在哪里加个处理器都会悄悄改变队列结构，且无法在代码之外审阅。
/// </para>
/// </summary>
public sealed class EventTopology
{
    private readonly List<EventSubscription> _subscriptions;

    private EventTopology(string exchangeName, List<EventSubscription> subscriptions)
    {
        ExchangeName = exchangeName;
        _subscriptions = subscriptions;
    }

    /// <summary>交换机名称。</summary>
    public string ExchangeName { get; }

    /// <summary>全部订阅。</summary>
    public IReadOnlyList<EventSubscription> Subscriptions => _subscriptions.AsReadOnly();

    /// <summary>全部队列名（消费队列 + 重试队列 + 死信队列），去重后有序。</summary>
    public IReadOnlyList<string> AllQueueNames =>
    [
        .. _subscriptions
            .SelectMany(s => new[] { s.QueueName }.Concat(s.RetryQueueNames).Append(s.DeadLetterQueueName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal),
    ];

    /// <summary>交换机到各消费队列的绑定：<c>(队列, 路由键)</c>。</summary>
    public IReadOnlyList<(string Queue, string RoutingKey)> Bindings =>
        [.. _subscriptions.Select(s => (s.QueueName, IntegrationEventNaming.RoutingKey(s.EventName)))];

    /// <summary>构造并校验拓扑。</summary>
    /// <param name="exchangeName">交换机名称。</param>
    /// <param name="subscriptions">订阅集合。</param>
    /// <returns>已校验的拓扑。</returns>
    /// <exception cref="ArgumentException">交换机名为空、订阅为空、出现重复订阅或非法延迟档位。</exception>
    public static EventTopology Create(string exchangeName, IEnumerable<EventSubscription> subscriptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName);
        ArgumentNullException.ThrowIfNull(subscriptions);

        var list = subscriptions.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("拓扑必须至少声明一个订阅。", nameof(subscriptions));
        }

        foreach (var subscription in list)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subscription.EventName);
            ArgumentException.ThrowIfNullOrWhiteSpace(subscription.ConsumerName);

            var tiers = subscription.RetryDelays.Select(EventSubscription.TierName).ToList();
            if (tiers.Count != tiers.Distinct(StringComparer.Ordinal).Count())
            {
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"订阅 {subscription.EventName}/{subscription.ConsumerName} 的延迟档位重复——重复的档位会指向同一个重试队列。"),
                    nameof(subscriptions));
            }

            if (subscription.RetryDelays.Any(static delay => delay < TimeSpan.FromSeconds(1)))
            {
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"订阅 {subscription.EventName}/{subscription.ConsumerName} 的延迟档位必须至少 1 秒。"),
                    nameof(subscriptions));
            }
        }

        var duplicates = list
            .GroupBy(static s => (s.EventName, s.ConsumerName))
            .Where(static group => group.Count() > 1)
            .Select(static group => $"{group.Key.EventName}/{group.Key.ConsumerName}")
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                $"同一 (事件, 消费端) 不能声明两次：{string.Join(", ", duplicates)}。", nameof(subscriptions));
        }

        return new EventTopology(exchangeName, list);
    }
}
