using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.Gateway;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// 路由表管理 API（票据 12）。
///
/// <para><b>它守的是"边缘的管理面必须有人看着"。</b>改一张路由表是系统级动作——
/// 加一条路由等于把某个上游暴露到边缘上。参照仓库的这套 API 因为认证方案没注册而
/// **必然失败**，也就是说那段代码从来没有真正工作过；而"从来没有工作过"与"被正确保护"
/// 在日志上看起来是一样的。</para>
/// </summary>
public sealed class GatewayRouteAdminTests : IClassFixture<GatewayRouteAdminApp>
{
    private readonly GatewayRouteAdminApp _app;

    /// <summary>非根管理员的令牌：身份为真、权限为无。</summary>
    private static string Token(bool root) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "nexusstack",
            audience: "nexusstack",
            claims: root ? [new Claim("nexusstack:root", "true")] : [new Claim("sub", "42")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GatewayRouteAdminApp.SigningKey)),
                SecurityAlgorithms.HmacSha256)));

    private static RouteDefinition SampleRoute(string routeId) => new()
    {
        RouteId = routeId,
        ClusterId = "platform",
        Path = "/api/identity/{**catch-all}",
        Methods = ["GET"],
        // **这一条是验收 2 的核心**：原项目的配置转换会**丢掉** Transforms，
        // 于是"配了却不变换"。这里放一个非空的转换，写完读回来必须还在。
        Transforms =
        [
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PathPattern"] = "/api/identity/{**catch-all}",
            },
        ],
    };

    public GatewayRouteAdminTests(GatewayRouteAdminApp app) => _app = app;

    /// <summary>**未认证 → 401。**</summary>
    [Fact]
    public async Task RouteAdmin_WithoutAToken_IsUnauthorized()
    {
        using var client = _app.CreateDefaultClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/gateway/routes/", UriKind.Relative),
            SampleRoute("unauth-probe"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// **已认证但不是根管理员 → 403。**
    ///
    /// <para>这一条比 401 更容易漏：一个"只要登录了就能改路由表"的实现，
    /// 在防住匿名者的同时把整个边缘交给了任何一个用户。</para>
    /// </summary>
    [Fact]
    public async Task RouteAdmin_WithANonRootToken_IsForbidden()
    {
        using var client = _app.CreateDefaultClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/gateway/routes/", UriKind.Relative))
        {
            Content = JsonContent.Create(SampleRoute("nonroot-probe")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(root: false));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// **根管理员可以 CRUD，而且改完立刻生效**（验收 1 + 2）。
    ///
    /// <para>"立刻生效"这一半不是锦上添花：网关原先在启动时一次性把路由拷进 YARP，
    /// 于是管理 API 会返回 204 而流量照旧走老规则——<b>接口说成功，事实没变</b>，
    /// 正是本仓反复记录的那种失效。</para>
    /// </summary>
    [Fact]
    public async Task RouteAdmin_AsRoot_CanCrud_AndChangesApplyImmediately()
    {
        using var client = _app.CreateDefaultClient();
        var root = Token(root: true);
        var routeId = "crud-" + Guid.NewGuid().ToString("N")[..8];

        // 一、新增 → 204。
        using (var created = await Send(client, HttpMethod.Post, "/gateway/routes/", root, SampleRoute(routeId)))
        {
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        }

        // 二、读回来：**Transforms 还在**——这是原项目的漏洞所在。
        using (var fetched = await Send(client, HttpMethod.Get, $"/gateway/routes/{routeId}", root, body: null))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

            var route = await fetched.Content.ReadFromJsonAsync<RouteDefinition>();
            Assert.NotNull(route);
            Assert.Equal(routeId, route.RouteId);
            Assert.Single(route.Transforms);
            Assert.Equal("/api/identity/{**catch-all}", route.Transforms[0]["PathPattern"]);
        }

        // 三、**它已经在 YARP 里了**：路由表自述端点看得到。
        using (var live = await client.GetAsync(new Uri("/gateway/routes", UriKind.Relative)))
        {
            // 那个端点不带鉴权（它是只读自述），所以这里不需要令牌。
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);

            var body = await live.Content.ReadAsStringAsync();
            Assert.Contains(routeId, body, StringComparison.Ordinal);
        }

        // 四、删除 → 204，再读 → 404。
        using (var deleted = await Send(client, HttpMethod.Delete, $"/gateway/routes/{routeId}", root, body: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var gone = await Send(client, HttpMethod.Get, $"/gateway/routes/{routeId}", root, body: null))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    /// <summary>重复的路由标识 → **409**，而不是静默覆盖。</summary>
    [Fact]
    public async Task AddingADuplicateRoute_Conflicts()
    {
        using var client = _app.CreateDefaultClient();
        var root = Token(root: true);
        var routeId = "dup-" + Guid.NewGuid().ToString("N")[..8];

        using (var first = await Send(client, HttpMethod.Post, "/gateway/routes/", root, SampleRoute(routeId)))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using var second = await Send(client, HttpMethod.Post, "/gateway/routes/", root, SampleRoute(routeId));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        RouteDefinition? body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }
}

/// <summary>起一个**配了签名密钥**的网关。</summary>
/// <remarks>
/// <para>密钥必须**在宿主启动之前**就在配置里——网关在启动时读它来决定用哪个认证方案
/// （验签 vs 明确降级）。而 <c>WebApplicationFactory.ConfigureAppConfiguration</c>
/// 是在应用自己的 <c>Program.cs</c> 跑完之后才生效的，所以这里只能用环境变量。</para>
///
/// <para>环境变量是进程级的，所以这个类**串行**跑（xUnit 默认同类内串行，
/// 而不同类之间可能并行——这个 key 只影响网关的验签，不改行为，可以接受）。</para>
/// </remarks>
public sealed class GatewayRouteAdminApp : WebApplicationFactory<GatewayHostMarker>
{
    /// <summary>测试签名密钥——够 32 字节。</summary>
    public const string SigningKey = "gateway-test-signing-key-long-enough-for-hs256";

    // **写在测试程序集自己的输出目录里，不写 `%TEMP%`。**
    // 第一版用了 `Path.GetTempPath()`，在受限环境下被拒绝（UnauthorizedAccessException）——
    // 而输出目录是测试进程本来就有权写的地方，每个测试运行一份独立文件。
    private readonly string _routeTablePath = Path.Combine(
        AppContext.BaseDirectory,
        $"gateway-routes-{Guid.NewGuid():N}.json");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Gateway:RouteTablePath", _routeTablePath);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 在宿主启动**之前**写下路由文件与密钥。
        File.WriteAllText(_routeTablePath, """
            {
              "routes": [],
              "clusters": [
                {
                  "clusterId": "platform",
                  "destinations": [
                    { "name": "primary", "address": "http://127.0.0.1:5199/" }
                  ]
                }
              ]
            }
            """);

        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "nexusstack");
        Environment.SetEnvironmentVariable("Jwt__Audience", "nexusstack");

        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Jwt__SigningKey", null);
            Environment.SetEnvironmentVariable("Jwt__Issuer", null);
            Environment.SetEnvironmentVariable("Jwt__Audience", null);

            if (File.Exists(_routeTablePath))
            {
                File.Delete(_routeTablePath);
            }
        }

        base.Dispose(disposing);
    }
}
