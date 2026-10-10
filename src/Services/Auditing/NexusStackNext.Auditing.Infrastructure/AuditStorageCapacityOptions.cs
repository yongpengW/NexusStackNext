namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>本实例的有限接纳配置；同一中央存储的生产副本须使用相同配置。</summary>
public sealed class AuditStorageCapacityOptions
{
    /// <summary>已提交事实记录上限。</summary>
    public long MaxFacts { get; init; } = 1_000_000;
    /// <summary>操作观察记录上限；开始和结束分别计量。</summary>
    public long MaxObservations { get; init; } = 2_000_000;
    /// <summary>接纳和容量诊断的等待预算，毫秒。</summary>
    public int WaitTimeoutMilliseconds { get; init; } = 3_000;

    internal AuditStorageCapacityOptions Validate()
    {
        if (MaxFacts is < 1 or > 1_000_000_000 || MaxObservations is < 1 or > 1_000_000_000
            || WaitTimeoutMilliseconds is < 50 or > 30_000)
        { throw new ArgumentException("Auditing:Capacity 记录额度须为 1..1000000000，等待须为 50..30000 毫秒。"); }
        return this;
    }
}
