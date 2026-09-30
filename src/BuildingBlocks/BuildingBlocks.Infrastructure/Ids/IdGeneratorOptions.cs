namespace NexusStackNext.BuildingBlocks.Infrastructure.Ids;

/// <summary>
/// 雪花 ID 生成器的配置。
/// <para>
/// <b>WorkerId 必须由配置统一下发，不能让各服务从本地 appsettings 各拿一个。</b>
/// 参照仓库就是这么做的：N 个微服务各自读本地配置，一旦有重复就会生成重复 ID，
/// 而这种冲突要等到写库时才以主键冲突的形式暴露。
/// </para>
/// </summary>
public sealed record IdGeneratorOptions
{
    /// <summary>允许的最大 WorkerId。</summary>
    public const int MaxWorkerId = 1023;

    /// <summary>本实例的机器位。<c>0..1023</c>，同一部署内各实例必须互不相同。</summary>
    public int WorkerId { get; init; }
}
