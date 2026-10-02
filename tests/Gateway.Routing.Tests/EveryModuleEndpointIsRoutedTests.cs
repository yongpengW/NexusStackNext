using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexusStackNext.Gateway.Routing.Tests;

/// <summary>
/// **模块里每一个 (HTTP 方法, 路径)，要么被网关的某条路由覆盖，要么被显式声明为"内部"。**
///
/// <para><b>第 29 轮加了"前缀"版，第 30 轮把它细化到方法。</b>因为实测发现：
/// Platform 的 <c>/api/platform/...</c> **有**路由，所以前缀版是绿的——
/// 而那条路由写死了 <c>methods: ["GET"]</c>，于是</para>
///
/// <code>
/// PUT    /api/platform/settings/{key}   → 经网关 405（直连后端 204）
/// DELETE /api/platform/settings/{key}   → 同上
/// </code>
///
/// <para>**"前缀有路由"和"这个端点打得通"是两件事。** 前者便宜好查，后者才是用户要的。</para>
///
/// <para><b>它和 <see cref="AnonymousEndpointsAreReachableTests"/> 是两条不同的检查</b>，
/// 缺一不可：那一条管"该匿名的端点有没有匿名路由"（`requireAuthentication`），
/// 这一条管"端点有没有路由可走"。第 27 轮的登录事故是前者的射程，
/// 第 30 轮的 Platform 写路径是后者的射程。</para>
/// </summary>
public sealed class EveryModuleEndpointIsRoutedOrDeclaredInternalTests
{
    /// <summary>
    /// **有意不经边缘暴露**的端点（方法 + 路径前缀）。
    ///
    /// <para>Auditing 整个前缀在里面，理由写在它自己的 ADR 里（只写上下文，
    /// 给它开边缘路由等于让任何人都能注入审计记录）。</para>
    ///
    /// <para><b>Platform 的写路径曾经也在这里</b>——那是票据 68 的临时状态：
    /// "没人能改一条设置"当时是事实，而它还没有被选中。现在选了"该经边缘改"：
    /// 路由表里多了 `platform-write`（PUT/DELETE，requireAuthentication: true），
    /// 于是那两行从这份清单里**移走**——它们有归宿了，而不是被豁免。</para>
    /// </summary>
    private static readonly (string Method, string PathPrefix)[] DeclaredInternal =
    [
        ("*", "/api/auditing"),
    ];

    [Theory]
    [InlineData("routes.json", "routes.pricing.json", "pricing")]
    [InlineData("routes.pricing.json", "routes.business.json", "costing")]
    public void OptionalContextVariant_PreservesTheCompleteBaselineConfiguration(string baselineFile, string variantFile, string context)
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "Gateway", "NexusStackNext.Gateway");
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, baselineFile)));
        var variant = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, variantFile)));
        Assert.NotNull(baseline);
        Assert.NotNull(variant);
        var routes = variant["routes"]!.AsArray();
        var clusters = variant["clusters"]!.AsArray();
        var pricingRoute = Assert.Single(routes, node => node!["routeId"]!.GetValue<string>() == context);
        var pricingCluster = Assert.Single(clusters, node => node!["clusterId"]!.GetValue<string>() == context + "-host");
        routes.Remove(pricingRoute);
        clusters.Remove(pricingCluster);
        // 对象字段顺序无关；数组顺序保留，尤其不能掩盖 transforms 的顺序变化。
        Assert.True(JsonNode.DeepEquals(baseline, variant),
            "上下文变体必须完整保留基线配置，包括鉴权、限流、超时、transforms 与目标地址。");
    }

    /// <summary>每个端点都要有归宿。</summary>
    [Theory]
    [InlineData("routes.json", false, false)]
    [InlineData("routes.pricing.json", true, false)]
    [InlineData("routes.business.json", true, true)]
    public void EveryModuleEndpoint_IsRoutedOrDeclaredInternal(string routeFile, bool includePricing, bool includeCosting)
    {
        var routes = LoadRoutes(routeFile);
        var allEndpoints = ModuleEndpoints();
        Assert.Contains(allEndpoints, endpoint => endpoint.Module == "PricingModule");
        Assert.Contains(allEndpoints, endpoint => endpoint.Module == "CostingModule");
        // 默认编排不启动 Pricing；启用样板时，必须同时保留全部平台路由。
        var endpoints = allEndpoints.Where(endpoint => (includePricing || endpoint.Module != "PricingModule")
            && (includeCosting || endpoint.Module != "CostingModule")).ToList();

        Assert.NotEmpty(routes);
        Assert.NotEmpty(endpoints);

        var orphans = new List<string>();

        foreach (var (module, method, path) in endpoints)
        {
            var covered = routes.Any(route =>
                RouteCovers(route.Path, path) && RouteAllowsMethod(route, method));

            var declared = DeclaredInternal.Any(entry =>
                (entry.Method == "*" || string.Equals(entry.Method, method, StringComparison.Ordinal))
                && path.StartsWith(entry.PathPrefix, StringComparison.Ordinal));

            if (!covered && !declared)
            {
                orphans.Add($"{module}：{method} {path}");
            }
        }

        Assert.True(
            orphans.Count == 0,
            "下列端点经边缘打不通，而且没有声明这是有意的——"
                + "**路由漏配与有意内部从外面看是一样的**，所以必须挑一个：\n  "
                + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// 一条路由是否覆盖某个具体路径。
    ///
    /// <para>规则照着 YARP 的形状来：路由路径里**第一个 <c>{</c> 之前**是它的基；
    /// **基以 <c>/</c> 结尾就是前缀匹配**，否则只匹配它自己。</para>
    ///
    /// <para>这条区分是有来历的：<c>/api/identity</c>（无斜杠）**不覆盖**
    /// <c>/api/identity/login</c>——第 27 轮的登录事故正是从这里漏出去的。</para>
    /// </summary>
    private static bool RouteCovers(string routePath, string endpointPath)
    {
        var brace = routePath.IndexOf('{', StringComparison.Ordinal);
        var basePath = brace < 0 ? routePath : routePath[..brace];

        if (basePath.EndsWith('/'))
        {
            return endpointPath.StartsWith(basePath, StringComparison.Ordinal)
                || string.Equals(endpointPath, basePath.TrimEnd('/'), StringComparison.Ordinal);
        }

        return string.Equals(endpointPath, basePath, StringComparison.Ordinal);
    }

    private static bool RouteAllowsMethod(RouteEntry route, string method)
    {
        if (route.Methods.Count == 0)
        {
            return true;
        }

        // `POST /api/scheduling/tasks/` 在模块里带尾斜杠，而路由里写的是不带的那条。
        return route.Methods.Contains(method, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把每个模块的 <c>MapGroup</c> 前缀与端点拼成完整路径。</summary>
    private static List<(string Module, string Method, string Path)> ModuleEndpoints()
    {
        var servicesRoot = Path.Combine(RepositoryRoot(), "src", "Services");
        var found = new List<(string, string, string)>();

        foreach (var module in Directory.EnumerateFiles(servicesRoot, "*Module.cs", SearchOption.AllDirectories))
        {
            if (module.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(module);
            var text = File.ReadAllText(module);

            foreach (Match group in Regex.Matches(text, @"(\w+)\s*=\s*endpoints\.MapGroup\(""([^""]+)""\)"))
            {
                var variable = group.Groups[1].Value;
                var prefix = group.Groups[2].Value;

                foreach (Match endpoint in Regex.Matches(
                    text, $@"{Regex.Escape(variable)}\.Map(Get|Post|Put|Delete)\(""([^""]*)"""))
                {
                    found.Add((name, endpoint.Groups[1].Value.ToUpperInvariant(), prefix + endpoint.Groups[2].Value));
                }
            }

            // 直接挂在 endpoints 上的绝对路径。
            foreach (Match endpoint in Regex.Matches(text, @"endpoints\.Map(Get|Post|Put|Delete)\(""(/api/[^""]*)"""))
            {
                found.Add((name, endpoint.Groups[1].Value.ToUpperInvariant(), endpoint.Groups[2].Value));
            }
        }

        return [.. found.Distinct()];
    }

    private static List<RouteEntry> LoadRoutes(string routeFile)
    {
        using var table = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                RepositoryRoot(), "src", "Gateway", "NexusStackNext.Gateway", routeFile)));

        return
        [
            .. table.RootElement.GetProperty("routes").EnumerateArray().Select(route =>
                new RouteEntry(
                    route.GetProperty("path").GetString() ?? string.Empty,
                    route.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array
                        ? [.. methods.EnumerateArray().Select(m => m.GetString() ?? string.Empty)]
                        : [])),
        ];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Services")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"从 {AppContext.BaseDirectory} 往上找不到仓库根。");
    }

    private sealed record RouteEntry(string Path, IReadOnlyList<string> Methods);
}
