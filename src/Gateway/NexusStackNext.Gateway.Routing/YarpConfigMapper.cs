using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace NexusStackNext.Gateway.Routing;

/// <summary>
/// 把 <see cref="GatewayRouteTable"/> 映射成 YARP 的运行时配置。
/// <para>
/// <b>这是一个纯函数</b>：给同一张路由表，永远得到同一份 YARP 配置。
/// 因此"配了转换却不生效"这类问题可以在没有边缘进程的情况下被发现——
/// 参照仓库的 <c>ConvertConfig</c>（<c>DynamicProxyConfigProvider.cs:102-152</c>）
/// 正是在这一步悄悄丢掉了 Transforms / HttpRequest / HttpClientConfig。
/// </para>
/// <para>
/// 为了防止"将来加了字段却忘了映射"，这里把已映射与刻意未映射的属性名都暴露出来，
/// 由一条反射测试守住两者之并必须等于模型的全部属性。
/// </para>
/// </summary>
public static class YarpConfigMapper
{
    /// <summary>要求已认证的授权策略名。由宿主注册同名策略。</summary>
    public const string AuthenticatedPolicy = "gateway.authenticated";

    /// <summary>默认限流策略名。由宿主注册同名限流器。</summary>
    public const string DefaultRateLimitPolicy = "gateway.default";

    /// <summary>已映射到 YARP 配置的 <see cref="RouteDefinition"/> 属性。</summary>
    public static IReadOnlySet<string> MappedRouteProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(RouteDefinition.RouteId),
        nameof(RouteDefinition.ClusterId),
        nameof(RouteDefinition.Path),
        nameof(RouteDefinition.Methods),
        nameof(RouteDefinition.Transforms),
        nameof(RouteDefinition.Timeout),
        nameof(RouteDefinition.RequireAuthentication),
        nameof(RouteDefinition.RateLimitPolicy),
    };

    /// <summary>
    /// <b>刻意不</b>映射到 YARP 的 <see cref="RouteDefinition"/> 属性。
    /// <para>目前为空——模型的每个字段都有去处。留这个集合是为了让"暂时不映射"也有明确位置，
    /// 而不是靠某个字段悄无声息地消失。</para>
    /// </summary>
    public static IReadOnlySet<string> DeliberatelyUnmappedRouteProperties { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>已映射的 <see cref="ClusterDefinition"/> 属性。</summary>
    public static IReadOnlySet<string> MappedClusterProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ClusterDefinition.ClusterId),
        nameof(ClusterDefinition.Destinations),
        nameof(ClusterDefinition.RequestTimeout),
    };

    /// <summary>刻意不映射的 <see cref="ClusterDefinition"/> 属性。</summary>
    public static IReadOnlySet<string> DeliberatelyUnmappedClusterProperties { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>已映射的 <see cref="DestinationDefinition"/> 属性。</summary>
    public static IReadOnlySet<string> MappedDestinationProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DestinationDefinition.Name),
        nameof(DestinationDefinition.Address),
    };

    /// <summary>刻意不映射的 <see cref="DestinationDefinition"/> 属性。</summary>
    public static IReadOnlySet<string> DeliberatelyUnmappedDestinationProperties { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>映射为 YARP 配置。</summary>
    /// <param name="table">已校验的路由表。</param>
    /// <returns>YARP 的路由与集群配置。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> 为 <c>null</c>。</exception>
    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) ToYarp(
        GatewayRouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return ([.. table.Routes.Select(ToRoute)], [.. table.Clusters.Select(ToCluster)]);
    }

    private static RouteConfig ToRoute(RouteDefinition route) => new()
    {
        RouteId = route.RouteId,
        ClusterId = route.ClusterId,
        Match = new RouteMatch
        {
            Path = route.Path,
            Methods = route.Methods.Count == 0 ? null : [.. route.Methods],
        },

        // 字段原样带过去，不做任何裁剪——上一次是被"顺手简化"丢掉的。
        Transforms = route.Transforms.Count == 0 ? null : route.Transforms,

        Timeout = route.Timeout,

        // 需要认证的路由挂上策略名。宿主在**没有配置认证处理器**时，策略必然失败
        // （用户未认证）——这就是 fail-closed：认证形态未定之前，受保护的路由不会变成公开的。
        AuthorizationPolicy = route.RequireAuthentication ? AuthenticatedPolicy : null,

        RateLimiterPolicy = route.RateLimitPolicy,
    };

    private static ClusterConfig ToCluster(ClusterDefinition cluster) => new()
    {
        ClusterId = cluster.ClusterId,
        Destinations = cluster.Destinations.ToDictionary(
            static destination => destination.Name,
            static destination => new DestinationConfig { Address = destination.Address },
            StringComparer.Ordinal),

        HttpRequest = cluster.RequestTimeout is { } timeout
            ? new ForwarderRequestConfig { ActivityTimeout = timeout }
            : null,
    };
}
