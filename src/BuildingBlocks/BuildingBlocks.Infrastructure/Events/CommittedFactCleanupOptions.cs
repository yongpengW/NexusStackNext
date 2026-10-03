namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>来源事实副本的维护策略；不是中央审计事实的保留政策。</summary>
public sealed record CommittedFactCleanupOptions
{
    /// <summary>是否启动自动维护，默认开启。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>首次发布确认后保留时长，默认七天，允许一小时至三百六十五天。</summary>
    public TimeSpan DeliveredRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>每轮最多删除条数，默认五百，允许一至一千。</summary>
    public int BatchSize { get; init; } = 500;
    /// <summary>轮次间隔，默认一分钟。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>每轮独立维护预算，默认五秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (DeliveredRetention < TimeSpan.FromHours(1) || DeliveredRetention > TimeSpan.FromDays(365)
            || BatchSize is < 1 or > 1000 || Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromHours(1)
            || Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("事实副本清理策略超出允许范围。");
        }
    }
}
