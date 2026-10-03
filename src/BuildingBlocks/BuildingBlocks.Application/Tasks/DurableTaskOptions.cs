namespace NexusStackNext.BuildingBlocks.Application.Tasks;

/// <summary>所属数据库裁决的有限执行、续租与重试策略。</summary>
public record DurableTaskOptions
{
    /// <summary>每次领取的租约，默认 30 秒。</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>单次领取的总租约预算，默认 30 分钟，最大 24 小时；续租不能移动此上界。</summary>
    public TimeSpan MaxLeaseDuration { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>一次自动重试预算内最多领取次数。</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>失败后的基础等待时间；按本轮尝试次数递增。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>每轮执行后的轮询间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>在启动前拒绝无界或无效策略。</summary>
    public void Validate()
    {
        if (LeaseDuration < TimeSpan.FromMilliseconds(100) || LeaseDuration > TimeSpan.FromMinutes(10)
            || MaxLeaseDuration < LeaseDuration || MaxLeaseDuration > TimeSpan.FromHours(24)
            || LeaseDuration.Ticks % 10 != 0 || MaxLeaseDuration.Ticks % 10 != 0
            || MaxAttempts is < 1 or > 10 || RetryDelay < TimeSpan.FromMilliseconds(10) || RetryDelay > TimeSpan.FromHours(1)
            || PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("持久化任务策略无效：租约与总预算须为整微秒且在允许范围内，并检查重试次数、等待与轮询间隔。");
        }
    }
}
