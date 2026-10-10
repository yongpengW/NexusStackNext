namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>中央操作观察的接收保留期；业务事实不使用此策略。</summary>
public sealed record OperationObservationRetentionOptions
{
    /// <summary>自动分批清理，默认启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>同一操作最后接收阶段后至少保留的时间，默认30天，允许1天至10年。</summary>
    public TimeSpan ObservationRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>每轮间隔，默认5分钟，允许1秒至1天。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>每批最多清理的操作数，默认500，允许1至1000；一个操作最多两个阶段。</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>验证宿主配置。</summary>
    public void Validate()
    {
        if (ObservationRetention < TimeSpan.FromDays(1) || ObservationRetention > TimeSpan.FromDays(3650)
            || Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromDays(1) || BatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("Auditing:Retention 的观察保留期须为1至3650天，间隔1秒至1天，批次1至1000个操作。");
        }
    }
}
