using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// 路由表校验。<b>重点是"问题一次列全"而不是"报第一个就返回"</b>——
/// 改配置的人应该一次看到所有错，而不是修一个跑一次。
/// </summary>
public sealed class RouteTableValidationTests
{
    private static ClusterDefinition Cluster(string id = "identity", params string[] addresses) => new()
    {
        ClusterId = id,
        Destinations = addresses.Length == 0
            ? [new DestinationDefinition("primary", "http://127.0.0.1:5001")]
            : [.. addresses.Select((address, index) => new DestinationDefinition($"d{index}", address))],
    };

    private static RouteDefinition Route(
        string routeId = "identity-api",
        string clusterId = "identity",
        string path = "/api/identity/{**catch-all}") => new()
    {
        RouteId = routeId,
        ClusterId = clusterId,
        Path = path,
    };

    [Fact]
    public void ValidTable_PassesWithNoProblems()
    {
        var problems = RouteTableValidator.Validate([Route()], [Cluster()]);

        Assert.Empty(problems);
    }

    [Fact]
    public void Create_ReturnsUsableTable_ForValidInput()
    {
        var result = GatewayRouteTable.Create([Route()], [Cluster()]);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Routes);
        Assert.Single(result.Value.Clusters);
    }

    [Fact]
    public void RoutePointingAtMissingCluster_IsRejected()
    {
        // 悬空引用：参照仓库从不校验这一点，仓库里那份"死"配置文件就是这样活着的。
        var problems = RouteTableValidator.Validate([Route(clusterId: "nonexistent")], [Cluster()]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.route.unknown_cluster");
    }

    [Fact]
    public void DuplicateRouteId_IsRejected()
    {
        var problems = RouteTableValidator.Validate([Route(), Route()], [Cluster()]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.route.duplicate_id");
    }

    [Fact]
    public void DuplicateClusterId_IsRejected()
    {
        var problems = RouteTableValidator.Validate([Route()], [Cluster(), Cluster()]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.cluster.duplicate_id");
    }

    [Fact]
    public void ClusterWithNoDestinations_IsRejected()
    {
        // 没有目标的集群只会吞掉请求。
        var empty = new ClusterDefinition { ClusterId = "identity", Destinations = [] };

        var problems = RouteTableValidator.Validate([Route()], [empty]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.cluster.no_destinations");
    }

    [Theory]
    [InlineData("identity:8080")]
    [InlineData("/relative/path")]
    [InlineData("ftp://host/path")]
    [InlineData("")]
    public void DestinationAddress_MustBeAbsoluteHttp(string address)
    {
        var problems = RouteTableValidator.Validate([Route()], [Cluster(addresses: address)]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.destination.invalid_address");
    }

    [Fact]
    public void DuplicateDestinationName_IsRejected()
    {
        var cluster = new ClusterDefinition
        {
            ClusterId = "identity",
            Destinations =
            [
                new DestinationDefinition("primary", "http://a:1"),
                new DestinationDefinition("primary", "http://b:2"),
            ],
        };

        var problems = RouteTableValidator.Validate([Route()], [cluster]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.destination.duplicate_name");
    }

    [Theory]
    [InlineData("api/identity")]
    [InlineData("")]
    public void Path_MustStartWithSlash(string path)
    {
        var problems = RouteTableValidator.Validate([Route(path: path)], [Cluster()]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.route.path_invalid");
    }

    [Fact]
    public void NonPositiveTimeouts_AreRejected()
    {
        var route = Route() with { Timeout = TimeSpan.Zero };
        var cluster = Cluster() with { RequestTimeout = TimeSpan.FromSeconds(-1) };

        var problems = RouteTableValidator.Validate([route], [cluster]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.route.timeout_invalid");
        Assert.Contains(problems, static problem => problem.Code == "gateway.cluster.timeout_invalid");
    }

    [Fact]
    public void Validate_ReportsEveryProblem_NotJustTheFirst()
    {
        // 参照仓库是"加载、吞异常、返回 Ok"，一处配置错误要修一个跑一次才能发现下一个。
        var broken = Route(routeId: "", clusterId: "missing", path: "no-slash");
        var emptyCluster = new ClusterDefinition { ClusterId = "identity", Destinations = [] };

        var problems = RouteTableValidator.Validate([broken], [emptyCluster]);

        Assert.True(problems.Count >= 3, $"期望至少 3 个问题，实际 {problems.Count} 个。");
        Assert.Contains(problems, static problem => problem.Code == "gateway.route.path_invalid");
        Assert.Contains(problems, static problem => problem.Code == "gateway.cluster.no_destinations");
    }

    [Fact]
    public void Create_FailsAsAWhole_WhenAnyProblemExists()
    {
        var result = GatewayRouteTable.Create([Route(clusterId: "missing")], [Cluster()]);

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.route_table.invalid", result.Error.Code);
    }

    [Fact]
    public void BlankRateLimitPolicyName_IsRejected()
    {
        // 写了策略名却写成空白 —— "以为限流开了、其实没开"。
        var route = Route() with { RateLimitPolicy = "   " };

        var problems = RouteTableValidator.Validate([route], [Cluster()]);

        Assert.Contains(problems, static problem => problem.Code == "gateway.route.rate_limit_policy_empty");
    }

    [Fact]
    public void RouteWithoutRateLimitPolicy_IsFine()
    {
        // null 表示"不限流"，是合法声明；空白字符串才是不合法。
        var problems = RouteTableValidator.Validate([Route()], [Cluster()]);

        Assert.DoesNotContain(problems, static problem => problem.Code == "gateway.route.rate_limit_policy_empty");
    }

    [Fact]
    public void RoutesRequireAuthentication_ByDefault()
    {
        // 与 ADR-0010 一致：边缘的默认答案是"要认证"，公开端点必须显式声明。
        Assert.True(Route().RequireAuthentication);
        Assert.False(Route() with { RequireAuthentication = false } is { RequireAuthentication: true });
    }
}
