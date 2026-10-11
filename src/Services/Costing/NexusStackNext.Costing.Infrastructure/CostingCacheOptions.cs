namespace NexusStackNext.Costing.Infrastructure;

/// <summary>Costing 展示查询的 Redis 缓存与回源预算。</summary>
public sealed class CostingCacheOptions
{
    /// <summary>显式启用 Redis；默认使用权威数据库读取。</summary>
    public bool Enabled { get; init; }
    /// <summary>只从环境或配置中心取得的 Redis 连接配置。</summary>
    public string ConnectionString { get; init; } = string.Empty;
    /// <summary>部署与数据库世代唯一的键命名空间。</summary>
    public string Namespace { get; init; } = string.Empty;
    /// <summary>每个进程最多同时执行的数据库回源数量。</summary>
    public int MaxConcurrentLoads { get; init; } = 16;
    /// <summary>一次数据库回源的最大时间。</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>缓存固定存活时间；读取不续期。</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>单次 Redis 操作等待预算。</summary>
    public TimeSpan RedisTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>持久失效扫描间隔。</summary>
    public TimeSpan InvalidationPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>拒绝无界预算及缺失配置。</summary>
    public void Validate()
    {
        if (MaxConcurrentLoads is < 1 or > 256 || LoadTimeout < TimeSpan.FromMilliseconds(100) || LoadTimeout > TimeSpan.FromSeconds(30))
        { throw new InvalidOperationException("Costing 查询预算无效。"); }
        if (Ttl < TimeSpan.FromSeconds(1) || Ttl > TimeSpan.FromMinutes(5)
            || RedisTimeout < TimeSpan.FromMilliseconds(50) || RedisTimeout > TimeSpan.FromSeconds(2)
            || InvalidationPollInterval < TimeSpan.FromMilliseconds(50) || InvalidationPollInterval > TimeSpan.FromSeconds(5))
        { throw new InvalidOperationException("Costing 缓存过期、超时或扫描间隔无效。"); }
        if (Enabled && (string.IsNullOrWhiteSpace(ConnectionString) || string.IsNullOrWhiteSpace(Namespace)
            || Namespace.Length > 80 || Namespace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
        { throw new InvalidOperationException("启用 Costing 缓存需要连接配置和只含字母、数字、横线或下划线的独立命名空间。"); }
    }
}
