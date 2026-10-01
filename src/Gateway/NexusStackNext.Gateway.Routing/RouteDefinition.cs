namespace NexusStackNext.Gateway.Routing;

/// <summary>一个上游目标。</summary>
/// <param name="Name">目标名称，在同一集群内唯一。</param>
/// <param name="Address">绝对地址，形如 <c>http://identity:8080</c>。</param>
public sealed record DestinationDefinition(string Name, string Address);

/// <summary>目标就绪探测。失败达到阈值后摘除，首次成功后恢复。</summary>
public sealed record DestinationHealthCheck
{
    /// <summary>目标的就绪端点路径。</summary>
    public string Path { get; init; } = "/health/ready";

    /// <summary>探测间隔。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单次探测超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>连续失败多少次后摘除。</summary>
    public int FailureThreshold { get; init; } = 2;
}

/// <summary>一组上游目标——通常对应一个后端服务。</summary>
public sealed record ClusterDefinition
{
    /// <summary>集群标识。</summary>
    public required string ClusterId { get; init; }

    /// <summary>目标列表，至少一个。</summary>
    public required IReadOnlyList<DestinationDefinition> Destinations { get; init; }

    /// <summary>该集群的请求超时。</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>默认探测就绪端点；显式设为 null 可关闭不支持探测的外部集群。</summary>
    public DestinationHealthCheck? HealthCheck { get; init; } = new();
}

/// <summary>
/// 一条路由。
/// <para>
/// <b>字段是齐的，一个都不少。</b>参照仓库的 <c>ConvertConfig</c>
/// （<c>DynamicProxyConfigProvider.cs:102-152</c>）在转换配置时丢掉了
/// Transforms / HttpRequest / HttpClientConfig —— 配了转换却不生效，且没有任何报错。
/// 这里把那些字段显式建模出来，并用一条 JSON 往返测试守住"不丢字段"。
/// </para>
/// </summary>
public sealed record RouteDefinition
{
    /// <summary>路由标识，全局唯一。</summary>
    public required string RouteId { get; init; }

    /// <summary>目标集群标识，必须存在。</summary>
    public required string ClusterId { get; init; }

    /// <summary>匹配路径，必须以 <c>/</c> 开头。</summary>
    public required string Path { get; init; }

    /// <summary>限制的 HTTP 方法；为空表示不限制。</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>
    /// 请求/响应转换。<b>形状与 YARP 一致：一个「字典列表」</b>，每个字典是一次转换
    /// （一个字典里可以有多个键，表示一次复合转换）。
    /// <para>
    /// 最初这里建模成了单个字典——那本身就是一种有损表达：YARP 允许同一路由挂多次转换，
    /// 单字典会把它们压成一次。参照仓库的 <c>ConvertConfig</c> 干脆把整个 Transforms 丢了
    /// （<c>DynamicProxyConfigProvider.cs:102-152</c>），配了转换却不生效且无报错。
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Transforms { get; init; } = [];

    /// <summary>该路由的请求超时。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// 限流策略名；<c>null</c> 表示不限制。
    /// <para>策略名必须在宿主里注册过——引用一个不存在的策略会让网关在启动时拒绝启动，
    /// 而不是等到流量打上来才发现限流没生效。</para>
    /// </summary>
    public string? RateLimitPolicy { get; init; }

    /// <summary>
    /// 是否要求已认证。
    /// <para>
    /// <b>默认 true。</b>与 ADR-0010 的取向一致：路由是"边缘的公开面"，
    /// 忘记声明时的默认答案是"要认证"，而不是"谁都能进"。
    /// 公开端点必须显式写成 <c>false</c>。
    /// </para>
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;
}
