using System.Text;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>消费处置策略：确认、降档重试、档位用尽进死信。</summary>
public sealed class ConsumePolicyTests
{
    private const string EventName = "identity.user-registered.v1";
    private const string ConsumerQueue = "identity.user-registered.v1.auditing";

    private static EventSubscription Subscription(params TimeSpan[] retryDelays) => new()
    {
        EventName = EventName,
        ConsumerName = "auditing",
        RetryDelays = retryDelays,
    };

    private static readonly TimeSpan[] ThreeTiers =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    [Fact]
    public void Handled_IsAcked_EvenAfterPreviousFailures()
    {
        var decision = ConsumePolicy.Decide(handled: true, attempts: 2, Subscription(ThreeTiers));

        Assert.Equal(DeliveryOutcome.Ack, decision.Outcome);
        Assert.Null(decision.RetryQueueName);
    }

    [Theory]
    [InlineData(0, "identity.user-registered.v1.auditing.retry.5s")]
    [InlineData(1, "identity.user-registered.v1.auditing.retry.30s")]
    [InlineData(2, "identity.user-registered.v1.auditing.retry.300s")]
    public void Failure_WalksUpTheTiers(int attempts, string expectedQueue)
    {
        var decision = ConsumePolicy.Decide(handled: false, attempts, Subscription(ThreeTiers));

        Assert.Equal(DeliveryOutcome.Retry, decision.Outcome);
        Assert.Equal(expectedQueue, decision.RetryQueueName);
    }

    [Fact]
    public void Failure_AfterAllTiers_GoesToDeadLetter()
    {
        var decision = ConsumePolicy.Decide(handled: false, attempts: 3, Subscription(ThreeTiers));

        Assert.Equal(DeliveryOutcome.DeadLetter, decision.Outcome);
        Assert.Contains("已用尽 3 个重试档位", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_WithNoTiersConfigured_GoesStraightToDeadLetter()
    {
        var decision = ConsumePolicy.Decide(handled: false, attempts: 0, Subscription());

        Assert.Equal(DeliveryOutcome.DeadLetter, decision.Outcome);
    }

    [Fact]
    public void NegativeAttempts_AreTreatedAsZero()
    {
        var decision = ConsumePolicy.Decide(handled: false, attempts: -5, Subscription(ThreeTiers));

        Assert.Equal(ConsumerQueue + ".retry.5s", decision.RetryQueueName);
    }

    [Fact]
    public void Decide_RejectsNullSubscription()
    {
        Assert.Throws<ArgumentNullException>(() => ConsumePolicy.Decide(true, 0, null!));
    }

    // ---------- 消息头 ----------

    [Fact]
    public void ReadAttempts_ReturnsZeroWhenHeaderMissing()
    {
        Assert.Equal(0, ConsumePolicy.ReadAttempts(null));
        Assert.Equal(0, ConsumePolicy.ReadAttempts(new Dictionary<string, object?>(StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void ReadAttempts_HandlesInt(int value)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal) { [ConsumePolicy.AttemptsHeader] = value };

        Assert.Equal(value, ConsumePolicy.ReadAttempts(headers));
    }

    [Fact]
    public void ReadAttempts_HandlesLongBytesAndString()
    {
        // 头值可能以 int / long / byte[] / string 到达，取决于发送端与 broker 版本。
        Assert.Equal(7, ConsumePolicy.ReadAttempts(Header(7L)));
        Assert.Equal(9, ConsumePolicy.ReadAttempts(Header(Encoding.UTF8.GetBytes("9"))));
        Assert.Equal(11, ConsumePolicy.ReadAttempts(Header("11")));
    }

    [Fact]
    public void ReadAttempts_FallsBackToZeroOnGarbage()
    {
        // 解析失败按 0 处理：宁可多重试一次，也不要因为一个解析失败把消息直接扔进死信。
        Assert.Equal(0, ConsumePolicy.ReadAttempts(Header("not-a-number")));
        Assert.Equal(0, ConsumePolicy.ReadAttempts(Header(new object())));
        Assert.Equal(0, ConsumePolicy.ReadAttempts(Header(-3)));
        Assert.Equal(0, ConsumePolicy.ReadAttempts(Header(null)));
    }

    [Fact]
    public void WithIncrementedAttempts_IncrementsAndKeepsOtherHeaders()
    {
        var original = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ConsumePolicy.AttemptsHeader] = 2,
            ["trace-id"] = "abc",
        };

        var next = ConsumePolicy.WithIncrementedAttempts(original);

        Assert.Equal(3, next[ConsumePolicy.AttemptsHeader]);
        Assert.Equal("abc", next["trace-id"]);

        // 不修改入参。
        Assert.Equal(2, original[ConsumePolicy.AttemptsHeader]);
    }

    [Fact]
    public void WithIncrementedAttempts_StartsFromZeroWhenMissing()
    {
        var next = ConsumePolicy.WithIncrementedAttempts(null);

        Assert.Equal(1, next[ConsumePolicy.AttemptsHeader]);
    }

    [Fact]
    public void IncrementThenRead_RoundTrips()
    {
        IReadOnlyDictionary<string, object?> headers = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var expected = 1; expected <= 3; expected++)
        {
            headers = ConsumePolicy.WithIncrementedAttempts(headers);
            Assert.Equal(expected, ConsumePolicy.ReadAttempts(headers));
        }
    }

    private static Dictionary<string, object?> Header(object? value) =>
        new(StringComparer.Ordinal) { [ConsumePolicy.AttemptsHeader] = value };
}
