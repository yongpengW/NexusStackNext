using System.Globalization;
using System.Text;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

/// <summary>一次投递的处置方式。</summary>
public enum DeliveryOutcome
{
    /// <summary>处理成功，确认。</summary>
    Ack,

    /// <summary>处理失败，进入某个重试档位。</summary>
    Retry,

    /// <summary>重试档位用尽，进入死信。</summary>
    DeadLetter,
}

/// <summary>处置决定。</summary>
/// <param name="Outcome">处置方式。</param>
/// <param name="RetryQueueName">重试队列名；仅当 <see cref="DeliveryOutcome.Retry"/> 时有值。</param>
/// <param name="Reason">可诊断的原因，会写进日志。</param>
public sealed record DeliveryDecision(DeliveryOutcome Outcome, string? RetryQueueName, string Reason)
{
    /// <summary>确认。</summary>
    /// <returns>决定。</returns>
    public static DeliveryDecision Ack() => new(DeliveryOutcome.Ack, null, "处理成功。");

    /// <summary>进入第 <paramref name="attempt"/> 个重试档位。</summary>
    /// <param name="queueName">重试队列名。</param>
    /// <param name="attempt">已失败次数（从 0 开始）。</param>
    /// <returns>决定。</returns>
    public static DeliveryDecision Retry(string queueName, int attempt) =>
        new(DeliveryOutcome.Retry, queueName, string.Create(
            CultureInfo.InvariantCulture,
            $"第 {attempt + 1} 次失败，进入重试档位 {queueName}。"));

    /// <summary>进入死信。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>决定。</returns>
    public static DeliveryDecision DeadLetter(string reason) => new(DeliveryOutcome.DeadLetter, null, reason);
}

/// <summary>
/// 消费端的处置策略：成功确认、失败降档重试、档位用尽进死信。
/// <para>
/// <b>纯函数，不碰 broker。</b>把"该不该重试、重试到哪个队列"从"怎么 Nack"里拆出来，
/// 是因为前者是容易被写错的部分（参照仓库正是死在这里），而后者只是几行 API 调用。
/// </para>
/// <para>
/// 重试档位按<b>尝试次数递增</b>选取：第 1 次失败进最短档，第 2 次进次长档，以此类推。
/// 用递增档位而不是固定档位，是因为持续失败通常意味着下游真的有问题，
/// 此时拉长间隔比反复快速重试更省资源。
/// </para>
/// </summary>
public static class ConsumePolicy
{
    /// <summary>记录已尝试次数的消息头。</summary>
    public const string AttemptsHeader = "nexusstack-attempts";

    /// <summary>决定一次投递的处置方式。</summary>
    /// <param name="handled">业务处理是否成功。</param>
    /// <param name="attempts">此前已失败的次数（从 0 开始）。</param>
    /// <param name="subscription">该消费端的订阅声明。</param>
    /// <returns>处置决定。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subscription"/> 为 <c>null</c>。</exception>
    public static DeliveryDecision Decide(bool handled, int attempts, EventSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (handled)
        {
            return DeliveryDecision.Ack();
        }

        var attempt = Math.Max(0, attempts);

        return attempt < subscription.RetryQueueNames.Count
            ? DeliveryDecision.Retry(subscription.RetryQueueNames[attempt], attempt)
            : DeliveryDecision.DeadLetter(string.Create(
                CultureInfo.InvariantCulture,
                $"已用尽 {subscription.RetryQueueNames.Count} 个重试档位，进入死信队列 {subscription.DeadLetterQueueName}。"));
    }

    /// <summary>
    /// 从消息头读已尝试次数。
    /// <para>宽容解析：RabbitMQ 的头值可能以 <c>int</c>、<c>long</c>、<c>byte[]</c> 或字符串到达，
    /// 取决于发送端与 broker 版本。无法识别时按 0 处理——<b>宁可多重试一次，也不要凭一个解析失败
    /// 就把消息直接扔进死信</b>。</para>
    /// </summary>
    /// <param name="headers">消息头。</param>
    /// <returns>已尝试次数；无法解析时为 0。</returns>
    public static int ReadAttempts(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(AttemptsHeader, out var raw) || raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int value => Math.Max(0, value),
            long value => (int)Math.Max(0, Math.Min(int.MaxValue, value)),
            byte[] bytes => Parse(Encoding.UTF8.GetString(bytes)),
            string text => Parse(text),
            _ => 0,
        };

        static int Parse(string text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;
    }

    /// <summary>返回一份把尝试次数加一的新消息头。<b>不修改入参。</b></summary>
    /// <param name="headers">原消息头。</param>
    /// <returns>新的消息头。</returns>
    public static IReadOnlyDictionary<string, object?> WithIncrementedAttempts(
        IReadOnlyDictionary<string, object?>? headers)
    {
        var next = headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(headers, StringComparer.Ordinal);

        next[AttemptsHeader] = ReadAttempts(headers) + 1;
        return next;
    }
}
