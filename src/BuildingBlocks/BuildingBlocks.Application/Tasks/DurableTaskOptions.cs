namespace NexusStackNext.BuildingBlocks.Application.Tasks;

/// <summary>有限本地计算的执行策略；不包含长任务心跳协议。</summary>
public record DurableTaskOptions
{
    /// <summary>每次领取的租约，默认 30 秒。</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
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
            || MaxAttempts is < 1 or > 10 || RetryDelay < TimeSpan.FromMilliseconds(10) || RetryDelay > TimeSpan.FromHours(1)
            || PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("持久化任务策略无效：检查租约、重试次数、等待与轮询间隔。");
        }
    }
}
