namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>来源已交付日志的清理政策；不适用于中央观察或业务事实。</summary>
public sealed record OperationJournalCleanupOptions
{
    /// <summary>宿主是否自动执行来源清理，默认启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>两轮清理间隔，默认一分钟，允许一秒至一小时。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>每轮清理预算，默认五秒，允许 50 毫秒至 30 秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>从首次发布确认时刻起保留，默认一天，允许一小时至三十天。</summary>
    public TimeSpan DeliveredRetention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>成功恢复凭据的保留期，默认三十天，允许一小时至一年；保存时固定到期时间。</summary>
    public TimeSpan RecoveryRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>每次清理的最大记录数，默认 500，允许 1 至 1000。</summary>
    public int BatchSize { get; init; } = 500;

    internal void Validate()
    {
        if (DeliveredRetention < TimeSpan.FromHours(1) || DeliveredRetention > TimeSpan.FromDays(30)
            || RecoveryRetention < TimeSpan.FromHours(1) || RecoveryRetention > TimeSpan.FromDays(365)
            || BatchSize is < 1 or > 1000 || Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromHours(1)
            || Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("OperationJournal:Cleanup 的交付保留期须为 1 小时至 30 天，恢复保留期为 1 小时至 365 天，批次 1 至 1000，间隔 1 秒至 1 小时，预算 50 毫秒至 30 秒。");
        }
    }
}
