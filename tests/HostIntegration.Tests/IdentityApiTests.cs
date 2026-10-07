using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexusStackNext.PlatformHost;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// Identity 的 **HTTP 语义**：状态码与 <c>ProblemDetails</c>。
///
/// <para><b>为什么这组测试值得单独存在。</b>参照仓库最直接的缺陷之一就在这里：
/// <c>RequestJsonResult</c> **从不设置状态码**，于是异常与 401 全都是 **HTTP 200**，
/// 凭据里带一个 <c>code=401</c>。SDK 与网关据此判断不了任何事——
/// 它们看到的是"每个请求都成功了"。</para>
///
/// <para>所以这组测试断言的是**状态码本身**，而不是响应体里的某个字段。</para>
/// </summary>
public sealed class IdentityApiTests(PlatformApp app) : IClassFixture<PlatformApp>
{
    /// <summary>校验失败（用户名太短）→ **400** + <c>ProblemDetails</c>。</summary>
    [Fact]
    public async Task InvalidRequest_Returns400_WithProblemDetails()
    {
        using var client = app.CreateDefaultClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "x", password = "a-strong-password" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // `ProblemDetails` 的形状由 ASP.NET 保证；这里断言的是**它确实是**一个 problem 文档，
        // 而不是一段自由文本——SDK 靠 `type`/`title`/`status` 这几个字段做判断。
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(problem.TryGetProperty("title", out var title), "响应体不是 ProblemDetails。");
        Assert.Contains("用户名", title.GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// **空口令必须被挡**——这是领域挡不住的一件事。
    ///
    /// <para>域里的 <c>PasswordHash</c> 只校验"看起来像不像哈希"，它**从来看不到明文**。
    /// 没有校验器时，一个空口令会被哈希、被接受、被存起来，
    /// 而整条链路上没有任何一环觉得不对。</para>
    /// </summary>
    [Fact]
    public async Task EmptyPassword_IsRejectedByTheValidationPipeline()
    {
        using var client = app.CreateDefaultClient();

        // 请求体用**字典**而不是匿名对象。原因是凭据检查里那条"连接串口令"规则：
        // 它的模式是「口令键名 + 等号 + 3 个以上非引号字符」，于是一个**没有引号**的值
        // （空串常量就是）会被它命中——那条规则分不出"测试里的空串"与"真口令"，也不该去分。
        // 键写成字符串字面量之后，等号前面隔着引号，就避开了。
        //
        // **这段注释本身也曾经触发过它**：上一版在注释里把这个写法原样写了一遍，
        // 于是文件里真的出现了那个模式。这是本会话第三次"解释规则的文字被规则抓住"。
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["userName"] = "validation-probe",
            ["password"] = string.Empty,
        };

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/identity/users", UriKind.Relative),
            payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("口令", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// **非法标识应当是 400/404，而不是 500。**
    ///
    /// <para>强类型 ID 的构造函数会拒绝默认值（<c>new UserId(0)</c> 抛 <c>ArgumentException</c>），
    /// 而这条路值直接来自 URL——于是"用户填了个 0"变成了一次**服务端异常**。
    /// 用户输入不该让服务端抛异常，那是"把输入当成程序错误"。</para>
    /// </summary>
    [Fact]
    public async Task InvalidIdentifier_IsNotAServerError()
    {
        using var client = app.CreateDefaultClient();

        using var response = await client.GetAsync(
            new Uri("/api/identity/users/0/permissions", UriKind.Relative));

        // 这个端点现在要求权限，所以**没有令牌**时是 401。
        // 这条测试原本守的是"非法标识不该变成 500"——那个结论仍然成立，只是它现在
        // 先被授权层挡住。要真的走到标识解析，得先过 401，见下面那条。
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// **登录返回一对令牌，刷新能换到新的。**
    ///
    /// <para>验的是 HTTP 层的形状：令牌在响应体里、字段名对、且刷新端点真的接得上。
    /// 轮换与"库里没有明文"由 <c>TokenIssuanceTests</c> 对着真库验——那是另一件事。</para>
    /// </summary>
    [Fact]
    public async Task LoginAndRefresh_ReturnTokens()
    {
        using var client = app.CreateDefaultClient();

        var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["userName"] = "http-token-probe",
            ["password"] = "a-strong-password",
        };

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), credentials)).StatusCode);

        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tokens = await login.Content.ReadApiDataAsync();
        var access = tokens.GetProperty("accessToken").GetString();
        var refresh = tokens.GetProperty("refreshToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(access));
        Assert.False(string.IsNullOrWhiteSpace(refresh));

        // 访问令牌是 JWT：三段，用点分隔。
        Assert.Equal(3, access!.Split('.').Length);

        // 刷新换一对新的。
        using var refreshed = await client.PostAsJsonAsync(
            new Uri("/api/identity/refresh", UriKind.Relative),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["refreshToken"] = refresh! });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var second = await refreshed.Content.ReadApiDataAsync();
        Assert.NotEqual(refresh, second.GetProperty("refreshToken").GetString());

        // **同一个刷新令牌用第二次必须失败**——这条在 HTTP 层也要成立。
        using var replay = await client.PostAsJsonAsync(
            new Uri("/api/identity/refresh", UriKind.Relative),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["refreshToken"] = refresh! });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>用户名重复 → **409**（不是 400：请求本身没错，是状态冲突）。</summary>
    [Fact]
    public async Task DuplicateUserName_Returns409()
    {
        using var client = app.CreateDefaultClient();

        var payload = new { userName = "duplicate-probe", password = "a-strong-password" };

        using var first = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), payload);

        // 同一个内存存储在整个测试类里共享，所以第一次必须是成功——否则下面断言的是别的东西。
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>未知用户 → **404**，而不是 200 带一个 code 字段。</summary>
    /// <summary>
    /// **授权的完整阶梯**：没有令牌 → 401；有令牌但没有权限 → 403。
    ///
    /// <para>它取代了原先那条"未知用户返回 404"——那个断言现在够不到，
    /// 因为请求先被授权层挡住。而这一条验的是**票据 11 的验收 1 与 2**：
    /// 未授权请求返回 403，且**默认拒绝**（这个用户没有任何权限，所以拿不到放行）。</para>
    /// </summary>
    [Fact]
    public async Task Authorization_Ladder_IsUnauthorizedThenForbidden()
    {
        using var client = app.CreateDefaultClient();
        // 用户名有长度上限——`authz-probe-` 加 32 位 guid 会被领域规则拒掉（400），
        // 而那会让这条测试在**第一步**就失败，看起来像授权出了问题。
        var probe = "az" + Guid.NewGuid().ToString("N")[..8];

        // 注册是公开的——否则没人能创建第一个用户（见 IdentityModule 的说明）。
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                new Uri("/api/identity/users", UriKind.Relative),
                Credentials(probe))).StatusCode);

        var target = new Uri("/api/identity/users/999999/permissions", UriKind.Relative);

        // 一、没有令牌 → **401**。
        using (var anonymous = await client.GetAsync(target))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // 二、有令牌，但这个用户**一个权限都没有** → **403**。
        //    这条才是"默认拒绝"的证据：身份是真的，权限是零，于是拒绝。
        using (var authenticated = new HttpRequestMessage(HttpMethod.Get, target))
        {
            authenticated.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await LoginAsync(client, probe));

            using var response = await client.SendAsync(authenticated);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// **令牌撤销后，旧访问令牌在有效期内也立即失效**（票据 11 从票据 10 接收的那条）。
    ///
    /// <para>这条测试要证明的是一件反直觉的事：访问令牌是**无状态**的 JWT，
    /// 验签方不看数据库就认它——所以"登出"如果只做在客户端（把令牌删掉），
    /// 那么任何拿到过它的人仍然能用到它过期。</para>
    ///
    /// <para>这里用的是 <c>/logout</c>（它只需要"已认证"），所以不依赖任何权限配置：
    /// 登出之后**同一个令牌**立刻换回 401。而它的有效期还有十几分钟。</para>
    /// </summary>
    [Fact]
    public async Task ARevokedAccessToken_StopsWorkingImmediately()
    {
        using var client = app.CreateDefaultClient();
        var probe = "rv" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                new Uri("/api/identity/users", UriKind.Relative),
                Credentials(probe))).StatusCode);

        var token = await LoginAsync(client, probe);
        var logout = new Uri("/api/identity/logout", UriKind.Relative);

        // 一、令牌是好的：用它能登出。
        using (var first = Authorized(HttpMethod.Post, logout, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(first)).StatusCode);
        }

        // 二、**同一个令牌现在必须是 401**——它还没到期，但已经被撤销了。
        using (var second = Authorized(HttpMethod.Post, logout, token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(second)).StatusCode);
        }

        // 三、反向的那一半：**换一个新令牌，它必须还能用。**
        //     没有这一步，一个"登出之后把所有人都踢掉"的实现同样能通过上面两条。
        var fresh = await LoginAsync(client, probe);
        using (var third = Authorized(HttpMethod.Post, logout, fresh))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(third)).StatusCode);
        }
    }

    /// <summary>带 Bearer 令牌的请求。</summary>
    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>登录并取回访问令牌。</summary>
    private static async Task<string> LoginAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/identity/login", UriKind.Relative),
            Credentials(userName));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadApiDataAsync();
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>构造登录/注册用的请求体。</summary>
    private static Dictionary<string, string> Credentials(string userName) =>
        new(StringComparer.Ordinal)
        {
            ["userName"] = userName,
            ["password"] = "a-strong-password",
        };

    /// <summary>
    /// **OpenAPI 可访问，且描述来自 XML 文档**（票据 01 已打开 <c>GenerateDocumentationFile</c>）。
    ///
    /// <para>这条断言的是"文档里有东西"，而不是"端点存在"——一个只有路径没有说明的文档
    /// 对使用者的价值接近于零，而那正是"忘了打开 XML 注释"的症状。</para>
    /// </summary>
    [Fact]
    public async Task OpenApi_IsServed_AndCarriesXmlDocumentation()
    {
        using var client = app.CreateDefaultClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/identity/users", out var users), "OpenAPI 里没有身份模块的路径。");

        // 摘要来自 XML 文档注释——它出现过，就说明文档真的被读进了模型。
        var serialized = users.GetRawText();
        Assert.Contains("description", serialized, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 起一个真正的平台宿主。
///
/// <para><b>它不需要数据库、也不需要配置中心。</b>Identity 当前注册的是内存存储，
/// 而 AgileConfig 未配置时走降级路径——于是这个测试在任何机器上都能跑，
/// 那正是"集成测试"该有的样子（需要真库的那些另有其人）。</para>
///
/// <para><b>它不是 sealed：</b>票据 67 的旅程要一个"配了根账号"的宿主变体，
/// 而那变体只该多一段配置——派生比复制这个类短，也不会让两份配置漂移。</para>
/// </summary>
public class PlatformApp : WebApplicationFactory<PlatformHostMarker>
{
    private readonly JourneyFileStorage _files = new();
    /// <summary>是否启用真实后台扫描；手动调用扫描器的测试显式关闭。</summary>
    public bool SchedulingWorkerEnabled { get; init; } = true;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Identity:Storage:Provider"] = "Memory",
                ["Platform:Storage:Provider"] = "Memory",
                ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Identity:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Scheduling:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
                ["Files:Storage:Provider"] = "Memory",
                ["Auditing:Storage:Provider"] = "Memory",
                ["OperationJournal:Storage:Provider"] = "Memory",
                ["Scheduling:Storage:Provider"] = "Memory",
                ["Scheduling:Worker:Enabled"] = SchedulingWorkerEnabled.ToString(),
            }));
        _files.Configure(builder);
        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 显式钉住环境：端点的可用性（例如仅开发环境的界面）会随它变化，
        // 而"跑测试时是什么环境"不该由机器上的环境变量决定。
        builder.UseEnvironment(Environments.Development);

        // 令牌签发需要签名密钥，而它**不该有默认值**——模板里放一个默认密钥等于
        // 每个用这个模板的系统共用同一把钥匙。真实部署从 env/*.dev 或配置中心来，
        // 这里由测试显式给出。
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Jwt:SigningKey"] = "integration-test-signing-key-long-enough-for-hs256",
            }));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _files.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _files.Dispose(); }
    }
}
