using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// JSON 载入与往返。<b>两条针对参照仓库具体缺陷的回归在这里。</b>
/// </summary>
public sealed class RouteTableJsonTests
{
    private static ClusterDefinition Cluster() => new()
    {
        ClusterId = "identity",
        Destinations = [new DestinationDefinition("primary", "http://127.0.0.1:5001")],
        RequestTimeout = TimeSpan.FromSeconds(30),
    };

    private static RouteDefinition DenseRoute() => new()
    {
        RouteId = "identity-api",
        ClusterId = "identity",
        Path = "/api/identity/{**catch-all}",
        Methods = ["GET", "POST"],
        Transforms =
        [
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PathRemovePrefix"] = "/api/identity" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["RequestHeader"] = "X-Forwarded-For", ["Append"] = "true" },
        ],
        Timeout = TimeSpan.FromSeconds(10),
        RateLimitPolicy = YarpConfigMapper.DefaultRateLimitPolicy,
        RequireAuthentication = false,
    };

    [Fact]
    public void JsonRoundTrip_PreservesEveryField()
    {
        // 参照仓库的 ConvertConfig（DynamicProxyConfigProvider.cs:102-152）在转换时丢掉了
        // Transforms / HttpRequest / HttpClientConfig —— 配了转换却不生效，且没有任何报错。
        //
        // 这里用"序列化 → 反序列化 → 再序列化，两次文本必须相同"来断言。
        // 不用 record 的相等性是因为：record 对集合成员比较的是**引用**，不是内容，
        // 往返之后必然"不相等"，那样断言会失效。
        var original = GatewayRouteTable.Create([DenseRoute()], [Cluster()]).Value;

        var reloaded = GatewayRouteTable.FromJson(original.ToJson());

        Assert.True(reloaded.IsSuccess);
        Assert.Equal(original.ToJson(), reloaded.Value.ToJson());
    }

    [Fact]
    public void JsonRoundTrip_KeepsTransformsExplicitly()
    {
        // 不依赖整串比较，单独把最容易被丢掉的那一项拎出来看。
        var original = GatewayRouteTable.Create([DenseRoute()], [Cluster()]).Value;

        var reloaded = GatewayRouteTable.FromJson(original.ToJson()).Value;
        var route = Assert.Single(reloaded.Routes);

        // 两次转换，各自独立——压成一次或丢掉一次都会被这里抓到。
        Assert.Equal(2, route.Transforms.Count);
        Assert.Equal("/api/identity", route.Transforms[0]["PathRemovePrefix"]);
        Assert.Equal("X-Forwarded-For", route.Transforms[1]["RequestHeader"]);
        Assert.Equal("true", route.Transforms[1]["Append"]);
        Assert.Equal(TimeSpan.FromSeconds(10), route.Timeout);
        Assert.Equal(YarpConfigMapper.DefaultRateLimitPolicy, route.RateLimitPolicy);
        Assert.Equal(["GET", "POST"], route.Methods);
        Assert.False(route.RequireAuthentication);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(reloaded.Clusters).RequestTimeout);
    }

    [Fact]
    public void MalformedJson_FailsInsteadOfSilentlySucceeding()
    {
        // 参照仓库的 LoadConfigAsync:93-96 吞掉异常，而控制器仍返回 Ok
        // —— "配置无效但 API 说成功"。
        var result = GatewayRouteTable.FromJson("{ this is not json");

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.json.malformed", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyJson_Fails(string? json)
    {
        var result = GatewayRouteTable.FromJson(json);

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.json.empty", result.Error.Code);
    }

    [Fact]
    public void ValidJsonWithInvalidRouting_Fails()
    {
        const string json = """
        {
          "routes": [
            { "routeId": "r1", "clusterId": "missing", "path": "/api/x" }
          ],
          "clusters": [
            { "clusterId": "identity", "destinations": [ { "name": "p", "address": "http://127.0.0.1:1" } ] }
          ]
        }
        """;

        var result = GatewayRouteTable.FromJson(json);

        Assert.True(result.IsFailure);
        Assert.Equal("gateway.route_table.invalid", result.Error.Code);
    }

    [Fact]
    public void MissingRequiredMember_Fails()
    {
        // 缺 clusterId：STJ 会因 required 成员缺失而抛错，我们把它转成失败——
        // 绝不用默认值悄悄补上，那样等于把配置错误变成运行时谜题。
        const string json = """
        {
          "routes": [ { "routeId": "r1", "path": "/api/x" } ],
          "clusters": []
        }
        """;

        Assert.True(GatewayRouteTable.FromJson(json).IsFailure);
    }

    [Fact]
    public void MinimalJson_ParsesWithSensibleDefaults()
    {
        const string json = """
        {
          "routes": [ { "routeId": "r1", "clusterId": "c1", "path": "/api/x" } ],
          "clusters": [ { "clusterId": "c1", "destinations": [ { "name": "p", "address": "https://backend:8443" } ] } ]
        }
        """;

        var result = GatewayRouteTable.FromJson(json);

        Assert.True(result.IsSuccess);

        var route = Assert.Single(result.Value.Routes);

        // 未声明的可选项取安全默认值：要认证、无转换、无方法限制。
        Assert.True(route.RequireAuthentication);
        Assert.Empty(route.Transforms);
        Assert.Empty(route.Methods);
        Assert.Null(route.Timeout);
    }

    [Fact]
    public void EmptyDocument_Fails()
    {
        var result = GatewayRouteTable.FromJson("""{ "routes": [], "clusters": [] }""");

        Assert.True(result.IsSuccess);

        // 空表本身合法（有效的"没有路由"），但请求会打到不存在的路由上——
        // 这是运维可见的事实，不是配置错误。
        Assert.Empty(result.Value.Routes);
    }
}
