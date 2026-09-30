using System.Globalization;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// Outbox 投递策略。
/// <para>
/// 退避表是<b>显式枚举</b>而不是指数公式：出故障时运维需要一眼看出"这条还要等多久"，
/// 指数公式给出的 <c>2^n</c> 秒既不直观也不可控。最后一档会被后续所有尝试复用。
/// </para>
/// </summary>
public sealed record OutboxDeliveryOptions
{
    /// <summary>默认配置。</summary>
    public static OutboxDeliveryOptions Default { get; } = new();

    /// <summary>单轮最多处理多少条。</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// 投递循环隔多久拉一次。
    ///
    /// <para><b>它是"多久才发现有新消息"的上界，不是吞吐的上界</b>——
    /// 一批最多 <see cref="BatchSize"/> 条，而循环不等投递完成就进入下一轮（它本来就是串行的，
    /// 所以这个间隔只在"上一轮空了"之后才真正等待）。</para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>超过此次数进入死信，不再重试。</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>退避表。第 n 次失败后等待 <c>BackoffSchedule[n-1]</c>，超出则复用最后一档。</summary>
    public IReadOnlyList<TimeSpan> BackoffSchedule { get; init; } =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    ];

    /// <summary>第 <paramref name="attemptCount"/> 次失败后应等待多久。</summary>
    /// <param name="attemptCount">已失败的次数，从 1 开始。</param>
    /// <returns>退避时长。</returns>
    /// <exception cref="InvalidOperationException">退避表为空。</exception>
    public TimeSpan BackoffFor(int attemptCount)
    {
        if (BackoffSchedule.Count == 0)
        {
            throw new InvalidOperationException("退避表不能为空。");
        }

        var index = Math.Clamp(attemptCount, 1, BackoffSchedule.Count) - 1;
        return BackoffSchedule[index];
    }

    /// <summary>校验配置是否可用。</summary>
    /// <exception cref="ArgumentOutOfRangeException">批次或尝试上限不是正数。</exception>
    /// <exception cref="InvalidOperationException">退避表为空。</exception>
    internal void Validate()
    {
        if (BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize), BatchSize, "批次大小必须是正数。");
        }
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "轮询间隔必须是正数。");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts), MaxAttempts, "最大尝试次数必须是正数。");
        }

        if (BackoffSchedule.Count == 0)
        {
            throw new InvalidOperationException("退避表不能为空。");
        }

        if (BackoffSchedule.Any(static delay => delay < TimeSpan.Zero))
        {
            throw new InvalidOperationException("退避时长不能为负。");
        }
    }

    /// <summary>便于日志输出的描述。</summary>
    /// <returns>描述文本。</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"OutboxDelivery(BatchSize={BatchSize}, MaxAttempts={MaxAttempts}, Tiers={BackoffSchedule.Count})");
}
