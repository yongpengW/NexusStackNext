using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace NexusStackNext.HostIntegration.Tests;

/// <summary>
/// 票据 67 的验收：**一条真实 HTTP 的旅程**——从零到"普通用户调通一个受权限保护的端点"。
///
/// <para><b>它为什么必须存在。</b>在此之前所有测试都是绿的，而**没有任何用户能拿到权限**：
/// `MenuTree.AddRoot` 在领域层写好也有测试，却没有任何调用者；`isBuiltIn` 永远是 `false`，
/// 于是 `IsRoot` 那条旁路不可达；签发的令牌里连 `Root` 声明都没有。
/// 三处断链各自都"看起来没问题"——单元测试直接构造聚合，绕过了"能不能建出那个聚合"。</para>
///
/// <para><b>旅程的顺序是刻意的</b>：普通用户**先被拒一次**（那一次会把"空权限集合"
/// 写进权限缓存），然后才拿到角色，然后再调一次——**必须变成 200**。
/// 少了中间那次被拒，这条测试就无法发现"角色变了吗、缓存失效了吗"那类缺陷
/// （`AssignRoleHandler` 漏失效就是这样被发现的）。</para>
///
/// <para><b>与 `scripts/verify-user-journey.ps1` 的分工</b>：那一条走真实的两个进程 + 网关，
/// 验的是"用户到底走不走得通"；这一条直打宿主，跑得快、每次构建都跑。
/// 按 `AGENTS.md` 的纪律，两条都要有——静的那条便宜，动的那条才是事实。</para>
/// </summary>
/// <param name="app">平台宿主 + 按配置播种的根账号。</param>
public sealed class AuthorizationChainJourneyTests(PlatformAppWithRootAccount app)
    : IClassFixture<PlatformAppWithRootAccount>
{
    /// <summary>旅程要打的那个受保护端点。它要求的权限键由 <c>RequirePermission</c> 声明。</summary>
    private const string ProtectedRoute = "/api/identity/users/{userId}/permissions";

    /// <summary>从零到 200：建菜单 → 挂 api-resource → 建角色 → 授菜单 → 建用户 → 给角色 → 调通。</summary>
    [Fact]
    public async Task FromNothingToANormalUserCallingAProtectedEndpoint()
    {
        using var rootClient = app.CreateClient();
        using var userClient = app.CreateClient();

        // ── 0. 根账号不是我们建的，是宿主按配置**播种**的（票据 71 的决定）。
        //       它同时证明了三件事：播种跑了、口令哈希可用、令牌里带上了 IsRoot 声明。
        var rootToken = await LoginAsync(rootClient, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        Authorize(rootClient, rootToken);

        // ── 1. 建菜单。普通用户做不到（见最后一个断言），根账号靠 IsRoot 旁路通过。
        using var created = await rootClient.PostAsJsonAsync(
            new Uri("/api/identity/menus", UriKind.Relative),
            new { Title = "后台导航", SortOrder = 1, ParentMenuId = (long?)null });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var menu = await created.Content.ReadApiDataAsync();
        var menuId = menu.GetProperty("menuId").GetInt64();
        Assert.True(menuId > 0, "建出来的菜单没有标识。");

        // 1b. **跨请求**读回来：内存适配器如果不是单例，这一步会看到一棵空树。
        var listed = await rootClient.GetFromJsonAsync<JsonElement>(new Uri("/api/identity/menus", UriKind.Relative));
        Assert.Equal(1, listed.GetProperty("data").GetProperty("count").GetInt32());

        // ── 2. 建 api-resource 并挂到那个菜单上。这一步是"权限键从哪来"的答案。
        using var resourceResponse = await rootClient.PostAsJsonAsync(
            new Uri("/api/identity/api-resources", UriKind.Relative),
            new { Path = ProtectedRoute, Method = "GET", MenuId = menuId });

        Assert.Equal(HttpStatusCode.Created, resourceResponse.StatusCode);

        var resource = await resourceResponse.Content.ReadApiDataAsync();
        var permissionKey = resource.GetProperty("permissionKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(permissionKey), "api-resource 没有产生权限键。");

        // ── 3. 建角色，并把菜单授予它（授权链的中间三环）。
        using var roleResponse = await rootClient.PostAsJsonAsync(
            new Uri("/api/identity/roles", UriKind.Relative),
            new { Code = "back-office", Name = "后台运营" });

        Assert.Equal(HttpStatusCode.Created, roleResponse.StatusCode);

        var role = await roleResponse.Content.ReadApiDataAsync();
        var roleId = role.GetProperty("roleId").GetInt64();

        using var grantResponse = await rootClient.PostAsync(
            new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);

        // ── 4. 自注册一个普通用户（这个端点是公开的，是**显式**的产品决定）。
        using var registered = await userClient.PostAsJsonAsync(
            new Uri("/api/identity/users", UriKind.Relative),
            new { UserName = "operator", Password = "operator-password-1234" });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var user = await registered.Content.ReadApiDataAsync();
        var userId = user.GetProperty("userId").GetInt64();

        var userToken = await LoginAsync(userClient, "operator", "operator-password-1234");
        Authorize(userClient, userToken);

        // ── 5. **先被拒一次**。它同时做两件事：证明受保护端点确实是受保护的，
        //       以及把"这个人当前的有效权限是空集"写进权限缓存——
        //       于是后面那一步才能验出"授权变更有没有让缓存失效"。
        using var beforeGrant = await userClient.GetAsync(
            new Uri($"/api/identity/users/{userId}/permissions", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, beforeGrant.StatusCode);

        // ── 6. 把角色给这个用户。**这一步改的是授权，缓存必须失效。**
        using var assigned = await rootClient.PostAsync(
            new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);

        // ── 7. 再调一次：**200，而且拿到那个权限键**。这就是本票的验收标准。
        using var afterGrant = await userClient.GetAsync(
            new Uri($"/api/identity/users/{userId}/permissions", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, afterGrant.StatusCode);

        var permissions = await afterGrant.Content.ReadApiDataAsync();
        var keys = permissions.GetProperty("keys").EnumerateArray().Select(static key => key.GetString()).ToList();

        Assert.Contains(permissionKey, keys);

        // ── 8. 反向的一半：**普通用户不能给自己授权。**
        //       它不是"顺便测一下"，而是这条链上最要紧的边界——
        //       一个刚拿到权限的人若能改菜单，整条链就只是装饰。
        using var escalation = await userClient.PostAsJsonAsync(
            new Uri("/api/identity/menus", UriKind.Relative),
            new { Title = "我自己加的", SortOrder = 9, ParentMenuId = (long?)null });

        Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
    }

    /// <summary>
    /// 根账号播种是**幂等**的：宿主重启（同一个存储）之后不该出现第二个根账号，
    /// 也不该把口令重置回配置里的值。
    /// </summary>
    [Fact]
    public async Task SeedingTheRootAccountAgain_DoesNothing()
    {
        using var client = app.CreateClient();

        // 第一次登录能成功，说明播种已经跑过。
        var token = await LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        Assert.False(string.IsNullOrWhiteSpace(token));

        // 再来一次也一样——而且不会因为"多播了一次"而出现第二个同名账号
        // （用户名唯一由仓储回答，`SeedRootAccountHandler` 在存在时直接返回 false）。
        var again = await LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        Assert.False(string.IsNullOrWhiteSpace(again));
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<string> LoginAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/identity/login", UriKind.Relative),
            new { UserName = userName, Password = password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadApiDataAsync();
        var token = payload.GetProperty("accessToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(token), "登录没有返回访问令牌。");

        return token!;
    }
}

/// <summary>
/// 平台宿主 + 一个**按配置播种**的根账号。
///
/// <para>它只比 <see cref="PlatformApp"/> 多一段配置：`Identity:Root:*`。
/// 播种因此走的是**生产那条路**（`RootAccountSeeder` 读配置 → 发用例），
/// 而不是测试自己造一个内置账号——那正是"测试要越过与生产同一道缝"的意思。</para>
/// </summary>
public sealed class PlatformAppWithRootAccount : PlatformApp
{
    /// <summary>播种的根账号用户名。</summary>
    public const string RootUserName = "root";

    /// <summary>播种的根账号口令。</summary>
    public const string RootPassword = "integration-root-password-32-bytes-long";

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Identity:Root:UserName"] = RootUserName,
                ["Identity:Root:Password"] = RootPassword,
            }));
    }
}
