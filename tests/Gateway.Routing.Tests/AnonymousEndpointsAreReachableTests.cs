using System.Text.Json;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// 网关的路由表必须让**该匿名可达的端点**匿名可达。
///
/// <para><b>这条检查来自第 27 轮 review 的一个真实事故。</b>
/// 路由表里有一条前缀路由：</para>
///
/// <code>
/// { "routeId": "identity-management", "path": "/api/identity/{**catch-all}",
///   "requireAuthentication": true }
/// </code>
///
/// <para>它把 <c>POST /api/identity/login</c> 也吞了进去——于是**登录本身需要令牌**。
/// 而"业务服务不对外暴露、边缘是唯一入口"是部署不变量（见 <c>AGENTS.md</c>），
/// 所以这不是"某条路少配了一个开关"，是**没有任何用户能拿到令牌**。</para>
///
/// <para>发现它的方式是**真实 HTTP 的端到端旅程**（<c>scripts/verify-user-journey.ps1</c>）：
/// 注册 401、登录 401，而直连后端 400（说明后端是活的）。
/// 现有的集成测试用的是 <c>WebApplicationFactory</c> 直打宿主，**跳过了网关的路由策略**——
/// 所以它们一条都不会红。</para>
///
/// <para><b>这条检查的射程是有限的</b>：它比对的是"有没有一条精确路径的匿名路由"，
/// 不是 YARP 真正的匹配结果。真正的匹配由端到端旅程守着。
/// 两者互补：这条便宜、每次构建都跑；那条贵、但验的是事实。</para>
/// </summary>
public sealed class AnonymousEndpointsAreReachableTests
{
    /// <summary>
    /// 产品上**必须**匿名可达的端点。
    ///
    /// <para>判据不是"它标了 AllowAnonymous"，而是"**没有令牌的人也得能走通它**"：
    /// 登录、刷新（用的是刷新令牌，不是访问令牌）、自注册（产品决定，见 Identity 模块）。</para>
    /// </summary>
    private static readonly (string Method, string Path)[] MustBeAnonymous =
    [
        ("POST", "/api/identity/login"),
        ("POST", "/api/identity/refresh"),
        ("POST", "/api/identity/users"),
    ];

    /// <summary>每个必须匿名的端点，都要有一条精确路径的匿名路由。</summary>
    [Theory]
    [InlineData("routes.json")]
    [InlineData("routes.pricing.json")]
    public void EveryEndpointThatMustBeAnonymous_HasAnAnonymousRoute(string routeFile)
    {
        var path = FindRouteTable(routeFile);
        using var table = JsonDocument.Parse(File.ReadAllText(path));

        var routes = table.RootElement.GetProperty("routes").EnumerateArray().ToList();
        Assert.NotEmpty(routes);

        var missing = new List<string>();

        foreach (var (method, endpoint) in MustBeAnonymous)
        {
            var match = routes.FirstOrDefault(route =>
                string.Equals(route.GetProperty("path").GetString(), endpoint, StringComparison.Ordinal)
                // 没写 methods 等于**所有方法**——那也覆盖了，但它同时意味着别的动词也走进来，
                // 所以这里要求精确路径就够了，方法交给下面单独看。
                && RouteAllowsMethod(route, method)
                && route.TryGetProperty("requireAuthentication", out var auth)
                && !auth.GetBoolean());

            if (match.ValueKind == JsonValueKind.Undefined)
            {
                missing.Add($"{method} {endpoint}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "下列端点必须经网关匿名可达，但没有对应的匿名路由——"
                + "边缘是唯一入口，所以这等于用户拿不到令牌：\n  "
                + string.Join("\n  ", missing)
                + $"\n（路由表：{path}）");
    }

    private static bool RouteAllowsMethod(JsonElement route, string method)
    {
        if (!route.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
        {
            // 没声明 methods = 全部方法。
            return true;
        }

        return methods.EnumerateArray()
            .Any(m => string.Equals(m.GetString(), method, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从测试输出目录往上找到仓库根，再定位网关的路由表。</summary>
    private static string FindRouteTable(string routeFile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Gateway", "NexusStackNext.Gateway", routeFile);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 往上找不到 src/Gateway/NexusStackNext.Gateway/{routeFile}。");
    }
}
