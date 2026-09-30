using System.Reflection;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// YARP 配置映射。<b>两条针对参照仓库 <c>ConvertConfig</c> 丢字段的回归在这里。</b>
/// </summary>
public sealed class YarpConfigMapperTests
{
    private static GatewayRouteTable Table() => GatewayRouteTable.Create(
        [
            new RouteDefinition
            {
                RouteId = "identity-api",
                ClusterId = "identity",
                Path = "/api/identity/{**catch-all}",
                Methods = ["GET", "POST"],
                Transforms =
                [
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["PathRemovePrefix"] = "/api/identity" },
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["ResponseHeader"] = "X-Served-By", ["Set"] = "identity" },
                ],
                Timeout = TimeSpan.FromSeconds(9),
                RateLimitPolicy = YarpConfigMapper.DefaultRateLimitPolicy,
            },
        ],
        [
            new ClusterDefinition
            {
                ClusterId = "identity",
                Destinations = [new DestinationDefinition("primary", "http://127.0.0.1:5001")],
                RequestTimeout = TimeSpan.FromSeconds(29),
            },
        ]).Value;

    [Fact]
    public void ToYarp_CarriesTransformsVerbatim()
    {
        // 参照仓库在这里丢掉整个 Transforms：配了转换却不生效，且没有任何报错。
        var (routes, _) = YarpConfigMapper.ToYarp(Table());

        var route = Assert.Single(routes);

        Assert.NotNull(route.Transforms);
        Assert.Equal(2, route.Transforms!.Count);
        Assert.Equal("/api/identity", route.Transforms[0]["PathRemovePrefix"]);
        Assert.Equal("identity", route.Transforms[1]["Set"]);
    }

    [Fact]
    public void ToYarp_CarriesMatchTimeoutAndDestinations()
    {
        var (routes, clusters) = YarpConfigMapper.ToYarp(Table());

        var route = Assert.Single(routes);
        var match = route.Match!;
        Assert.Equal("identity-api", route.RouteId);
        Assert.Equal("identity", route.ClusterId);
        Assert.Equal("/api/identity/{**catch-all}", match.Path);
        Assert.Equal(["GET", "POST"], match.Methods);
        Assert.Equal(TimeSpan.FromSeconds(9), route.Timeout);

        var cluster = Assert.Single(clusters);
        Assert.Equal("identity", cluster.ClusterId);
        Assert.Equal("http://127.0.0.1:5001", cluster.Destinations!["primary"]!.Address);
        Assert.Equal(TimeSpan.FromSeconds(29), cluster.HttpRequest?.ActivityTimeout);
    }

    [Fact]
    public void ToYarp_LeavesOptionalSectionsNull_WhenTheModelHasNothing()
    {
        var table = GatewayRouteTable.Create(
            [new RouteDefinition { RouteId = "r", ClusterId = "c", Path = "/x" }],
            [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition("p", "http://a:1")] }]).Value;

        var (routes, clusters) = YarpConfigMapper.ToYarp(table);

        Assert.Null(Assert.Single(routes).Transforms);
        Assert.Null(Assert.Single(routes).Match!.Methods);
        Assert.Null(Assert.Single(routes).Timeout);
        Assert.Null(Assert.Single(clusters).HttpRequest);
    }

    [Fact]
    public void EveryModelProperty_IsEitherMappedOrDeliberatelyExcluded()
    {
        // 这条测试的存在理由：防止"将来给模型加了字段，却忘了在映射里带上"。
        // 参照仓库的丢字段不是有人故意删的，就是漏了——靠人记住是不够的。
        AssertCoversEverything(
            typeof(RouteDefinition),
            YarpConfigMapper.MappedRouteProperties,
            YarpConfigMapper.DeliberatelyUnmappedRouteProperties);

        AssertCoversEverything(
            typeof(ClusterDefinition),
            YarpConfigMapper.MappedClusterProperties,
            YarpConfigMapper.DeliberatelyUnmappedClusterProperties);

        AssertCoversEverything(
            typeof(DestinationDefinition),
            YarpConfigMapper.MappedDestinationProperties,
            YarpConfigMapper.DeliberatelyUnmappedDestinationProperties);
    }

    [Fact]
    public void RequireAuthentication_MapsToAnAuthorizationPolicy()
    {
        // 认证形态（票据 10）还没定，但这不代表受保护的路由就该公开。
        // 挂上策略名之后，宿主只要**没有**配置认证处理器，策略就必然失败——
        // 未定状态的默认行为是拒绝，而不是放行（ADR-0010）。
        var (routes, _) = YarpConfigMapper.ToYarp(Table());

        Assert.Equal(YarpConfigMapper.AuthenticatedPolicy, Assert.Single(routes).AuthorizationPolicy);
    }

    [Fact]
    public void PublicRoute_CarriesNoAuthorizationPolicy()
    {
        var table = GatewayRouteTable.Create(
            [new RouteDefinition { RouteId = "r", ClusterId = "c", Path = "/x", RequireAuthentication = false }],
            [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition("p", "http://a:1")] }]).Value;

        var (routes, _) = YarpConfigMapper.ToYarp(table);

        Assert.Null(Assert.Single(routes).AuthorizationPolicy);
    }

    [Fact]
    public void RateLimitPolicy_MapsToYarpRateLimiterPolicy()
    {
        // 参照仓库的网关是裸转发，没有任何限流。
        var (routes, _) = YarpConfigMapper.ToYarp(Table());

        Assert.Equal(YarpConfigMapper.DefaultRateLimitPolicy, Assert.Single(routes).RateLimiterPolicy);
    }

    [Fact]
    public void RouteWithoutRateLimitPolicy_CarriesNone()
    {
        var table = GatewayRouteTable.Create(
            [new RouteDefinition { RouteId = "r", ClusterId = "c", Path = "/x" }],
            [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition("p", "http://a:1")] }]).Value;

        var (routes, _) = YarpConfigMapper.ToYarp(table);

        Assert.Null(Assert.Single(routes).RateLimiterPolicy);
    }

    [Fact]
    public void ToYarp_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => YarpConfigMapper.ToYarp(null!));
    }

    private static void AssertCoversEverything(Type model, IReadOnlySet<string> mapped, IReadOnlySet<string> unmapped)
    {
        var declared = model
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod is not null || property.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null)
            .Select(static property => property.Name)
            .Where(static name => name != "EqualityContract")
            .Order(StringComparer.Ordinal)
            .ToList();

        var covered = mapped.Concat(unmapped).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(declared, covered);
    }
}
