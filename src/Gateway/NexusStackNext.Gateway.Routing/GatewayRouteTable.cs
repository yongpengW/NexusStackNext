using System.Globalization;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Gateway.Routing;

/// <summary>路由表的校验规则。</summary>
public static class RouteTableValidator
{
    /// <summary>
    /// 校验路由表，<b>返回全部问题</b>而不是第一个。
    /// <para>
    /// 参照仓库是"加载、吞掉异常、控制器返回 Ok"（<c>LoadConfigAsync:93-96</c>），
    /// 于是配置写错了却得到"API 成功"。这里改成把问题一次列全：
    /// 改配置的人应该一次看到所有错，而不是修一个跑一次。
    /// </para>
    /// </summary>
    /// <param name="routes">路由。</param>
    /// <param name="clusters">集群。</param>
    /// <returns>问题列表；为空表示通过。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    public static IReadOnlyList<Error> Validate(
        IReadOnlyList<RouteDefinition> routes,
        IReadOnlyList<ClusterDefinition> clusters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);

        var problems = new List<Error>();
        // YARP 的标识匹配不区分大小写；这里必须使用相同的唯一性规则。
        var clusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.ClusterId))
            {
                problems.Add(new Error("gateway.cluster.id_empty", "集群标识不能为空。"));
            }
            else if (!clusterIds.Add(cluster.ClusterId))
            {
                problems.Add(new Error(
                    "gateway.cluster.duplicate_id",
                    $"集群标识重复：{cluster.ClusterId}。"));
            }

            // 即便标识有问题，也继续检查这个集群的内部一致性——
            // 标识为空时用 continue 跳过，等于把"一次列全"退化成"只报第一个"。
            ValidateCluster(cluster, problems);
        }

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.RouteId))
            {
                problems.Add(new Error("gateway.route.id_empty", "路由标识不能为空。"));
            }
            else if (!routeIds.Add(route.RouteId))
            {
                problems.Add(new Error("gateway.route.duplicate_id", $"路由标识重复：{route.RouteId}。"));
            }

            // 悬空引用：参照仓库从不校验这一点，配置文件里那份"死文件"就是这样活着的。
            if (!clusterIds.Contains(route.ClusterId))
            {
                problems.Add(new Error(
                    "gateway.route.unknown_cluster",
                    $"路由 {route.RouteId} 指向不存在的集群：{route.ClusterId}。"));
            }

            if (string.IsNullOrWhiteSpace(route.Path) || !route.Path.StartsWith('/'))
            {
                problems.Add(new Error(
                    "gateway.route.path_invalid",
                    $"路由 {route.RouteId} 的匹配路径必须以 / 开头：{route.Path}。"));
            }

            if (route.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            {
                problems.Add(new Error(
                    "gateway.route.timeout_invalid",
                    $"路由 {route.RouteId} 的超时必须为正。"));
            }

            if (route.MaxRequestBodySize is <= 0)
            {
                problems.Add(new Error("gateway.route.body_limit_invalid", $"路由 {route.RouteId} 的请求体上限必须为正。"));
            }

            // 写了策略名却写成空白，等于"以为限流开了、其实没开"。
            if (route.RateLimitPolicy is not null && string.IsNullOrWhiteSpace(route.RateLimitPolicy))
            {
                problems.Add(new Error(
                    "gateway.route.rate_limit_policy_empty",
                    $"路由 {route.RouteId} 声明了限流策略但名称为空。"));
            }
        }

        return problems;
    }

    private static void ValidateCluster(ClusterDefinition cluster, List<Error> problems)
    {
        if (cluster.HealthCheck is { } health &&
            (string.IsNullOrWhiteSpace(health.Path) || !health.Path.StartsWith('/') ||
             health.Interval <= TimeSpan.Zero || health.Timeout <= TimeSpan.Zero || health.FailureThreshold <= 0))
        {
            problems.Add(new Error("gateway.cluster.health_invalid",
                $"集群 {cluster.ClusterId} 的探测路径必须以 / 开头，间隔、超时和失败阈值必须为正。"));
        }

        if (cluster.Destinations.Count == 0)
        {
            problems.Add(new Error(
                "gateway.cluster.no_destinations",
                $"集群 {cluster.ClusterId} 至少要有一个目标——没有目标的集群只会吞掉请求。"));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in cluster.Destinations)
        {
            if (string.IsNullOrWhiteSpace(destination.Name))
            {
                problems.Add(new Error("gateway.destination.name_empty", $"集群 {cluster.ClusterId} 下有目标缺少名称。"));
            }
            else if (!names.Add(destination.Name))
            {
                problems.Add(new Error(
                    "gateway.destination.duplicate_name",
                    $"集群 {cluster.ClusterId} 下目标名称重复：{destination.Name}。"));
            }

            if (!IsAbsoluteHttpAddress(destination.Address))
            {
                problems.Add(new Error(
                    "gateway.destination.invalid_address",
                    $"目标 {cluster.ClusterId}/{destination.Name} 的地址必须是绝对的 http/https 地址：{destination.Address}。"));
            }
        }

        if (cluster.RequestTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            problems.Add(new Error(
                "gateway.cluster.timeout_invalid",
                $"集群 {cluster.ClusterId} 的超时必须为正。"));
        }
    }

    private static bool IsAbsoluteHttpAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// 网关路由表。<b>只能由校验通过的输入构造</b>——不存在"半有效"的实例在系统里流转。
/// </summary>
public sealed class GatewayRouteTable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private GatewayRouteTable(IReadOnlyList<RouteDefinition> routes, IReadOnlyList<ClusterDefinition> clusters)
    {
        Routes = routes;
        Clusters = clusters;
    }

    /// <summary>路由。</summary>
    public IReadOnlyList<RouteDefinition> Routes { get; }

    /// <summary>集群。</summary>
    public IReadOnlyList<ClusterDefinition> Clusters { get; }

    /// <summary>构造并校验路由表。</summary>
    /// <param name="routes">路由。</param>
    /// <param name="clusters">集群。</param>
    /// <returns>合法时返回路由表；否则返回带问题清单的失败。</returns>
    public static Result<GatewayRouteTable> Create(
        IEnumerable<RouteDefinition> routes,
        IEnumerable<ClusterDefinition> clusters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);

        var routeList = routes.ToList();
        var clusterList = clusters.ToList();

        var problems = RouteTableValidator.Validate(routeList, clusterList);
        if (problems.Count > 0)
        {
            return Result.Failure<GatewayRouteTable>(new Error(
                "gateway.route_table.invalid",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"路由表有 {problems.Count} 个问题：{string.Join("；", problems.Select(static problem => problem.Code))}")));
        }

        return Result.Success(new GatewayRouteTable(routeList, clusterList));
    }

    /// <summary>从 JSON 载入并校验。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>合法时返回路由表；否则返回失败。</returns>
    public static Result<GatewayRouteTable> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result.Failure<GatewayRouteTable>(new Error("gateway.json.empty", "路由配置为空。"));
        }

        RouteTableDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RouteTableDocument>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            // 关键：**不吞异常、不返回"成功"**。参照仓库正是在这里吞掉错误而控制器返回 Ok，
            // 于是"配置无效但 API 成功"。
            return Result.Failure<GatewayRouteTable>(new Error(
                "gateway.json.malformed",
                $"路由配置不是合法 JSON：{exception.Message}"));
        }

        return document is null
            ? Result.Failure<GatewayRouteTable>(new Error("gateway.json.empty", "路由配置解析为 null。"))
            : Create(document.Routes ?? [], document.Clusters ?? []);
    }

    /// <summary>序列化为 JSON。</summary>
    /// <returns>JSON 文本。</returns>
    public string ToJson() => JsonSerializer.Serialize(
        new RouteTableDocument { Routes = Routes, Clusters = Clusters },
        SerializerOptions);

    private sealed record RouteTableDocument
    {
        public IReadOnlyList<RouteDefinition>? Routes { get; init; }

        public IReadOnlyList<ClusterDefinition>? Clusters { get; init; }
    }
}
