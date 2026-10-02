namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>Pricing 查询缓存及每宿主回源预算；不影响命令、任务或权限判定。</summary>
public sealed class PricingCacheOptions
{
    /// <summary>显式启用 Redis；默认直接回源。</summary>
    public bool Enabled { get; init; }
    /// <summary>仅从环境或配置中心取得的 Redis 连接配置。</summary>
    public string ConnectionString { get; init; } = string.Empty;
    /// <summary>部署与数据库世代唯一的命名空间，数据库恢复后必须轮换。</summary>
    public string Namespace { get; init; } = string.Empty;
    /// <summary>每个宿主最多同时执行的定价回源数量；没有等待队列。</summary>
    public int MaxConcurrentLoads { get; init; } = 16;
    /// <summary>一次回源的最大时间。</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>缓存固定存活时间；读取不续期，默认一分钟。</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Redis 操作等待预算；迟到操作仍受令牌保护。</summary>
    public TimeSpan RedisTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>持久失效扫描间隔。</summary>
    public TimeSpan InvalidationPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>拒绝无界策略及缺失配置。</summary>
    public void Validate()
    {
        if (MaxConcurrentLoads is < 1 or > 256 || LoadTimeout < TimeSpan.FromMilliseconds(100) || LoadTimeout > TimeSpan.FromSeconds(30))
        { throw new InvalidOperationException("Pricing 查询预算无效。"); }
        if (Ttl < TimeSpan.FromSeconds(1) || Ttl > TimeSpan.FromMinutes(5)
            || RedisTimeout < TimeSpan.FromMilliseconds(50) || RedisTimeout > TimeSpan.FromSeconds(2)
            || InvalidationPollInterval < TimeSpan.FromMilliseconds(50) || InvalidationPollInterval > TimeSpan.FromSeconds(5))
        { throw new InvalidOperationException("Pricing 缓存过期、超时或扫描间隔无效。"); }
        if (Enabled && (string.IsNullOrWhiteSpace(ConnectionString) || string.IsNullOrWhiteSpace(Namespace)
            || Namespace.Length > 80 || Namespace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
        { throw new InvalidOperationException("启用 Pricing 缓存需要连接配置和只含字母、数字、横线的独立命名空间。"); }
    }
}
