namespace NexusStackNext.Pricing.Application;

/// <summary>导出领取的有界租约策略；生成期间不持有数据库事务。</summary>
public sealed class PricingExportOptions
{
    /// <summary>每次领取及续租的有限期限。</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>一次执行代次的固定最长期限。</summary>
    public TimeSpan MaxExecutionDuration { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>一次自动预算内的最大领取次数。</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>生成器的实际字节预算，最多 32 MiB。</summary>
    public long MaxOutputBytes { get; init; } = 33_554_432;
    /// <summary>无工作或失败后的有界轮询间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>启动时拒绝无界或矛盾的策略。</summary>
    public void Validate()
    {
        if (LeaseDuration < TimeSpan.FromMilliseconds(100) || LeaseDuration > TimeSpan.FromMinutes(2)
            || MaxExecutionDuration <= LeaseDuration || MaxExecutionDuration > TimeSpan.FromMinutes(10)
            || MaxAttempts is < 1 or > 10 || MaxOutputBytes is < 1 or > 33_554_432
            || PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromSeconds(30))
        { throw new InvalidOperationException("Pricing:Exports 的租约策略无效。"); }
    }
}
