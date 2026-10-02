using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PlatformSettingsAccessTests
{
    [Theory]
    [InlineData("routes.json")]
    [InlineData("routes.pricing.json")]
    [InlineData("routes.business.json")]
    public async Task ShippedGatewayRoutes_RequireAuthentication_AndForwardAuthorizedSettingsRead(string configurationFile)
    {
        await using var platform = new PlatformAppWithRootAccount();
        platform.UseKestrel(0);
        using var direct = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(direct.BaseAddress!.AbsoluteUri)
        {
            SigningKey = "integration-test-signing-key-long-enough-for-hs256",
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        var settingsRoute = Assert.Single(shipped.Routes, route => route.RouteId == "platform-read");
        Assert.True(settingsRoute.RequireAuthentication);
        gateway.UseRoutes([settingsRoute with { ClusterId = "backend", RateLimitPolicy = null }]);
        using var client = gateway.CreateClient();
        using var anonymous = await client.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await LoginAsync(direct, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        client.DefaultRequestHeaders.Authorization = direct.DefaultRequestHeaders.Authorization;
        using var allowed = await client.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [PostgresFact]
    public async Task SessionAuthorityUnavailable_DoesNotAllowSettingsAccess_AndRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "settings-root-password");
        using var client = app.CreateClient();
        await LoginAsync(client, "journey-root", "settings-root-password");
        using var before = await client.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await database.SetAvailableAsync(false);
        try
        {
            using var unavailable = await client.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
            Assert.Equal(HttpStatusCode.InternalServerError, unavailable.StatusCode);
            var problem = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("http.500", problem.GetProperty("errorCode").GetString());
            Assert.False((await unavailable.Content.ReadAsStringAsync()).Contains(database.ConnectionString, StringComparison.Ordinal));
        }
        finally
        {
            await database.SetAvailableAsync(true);
        }
        using var recovered = await client.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsReader_CanReadGrantedRoutes_ButCannotWrite_AndRevocationRejectsExistingToken(bool disableAccount)
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var written = await root.PutAsJsonAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative), new { value = "test-sender" });
        Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);

        var created = await CreateAsync(user, "/api/identity/users", new { userName = "reader", password = "settings-test-password" });
        var userId = created.GetProperty("userId").ReadHttpInt64();
        await LoginAsync(user, "reader", "settings-test-password");
        using var denied = await user.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Settings read", sortOrder = 1 });
        var menuId = menu.GetProperty("menuId").ReadHttpInt64();
        foreach (var path in new[] { "/api/platform/settings", "/api/platform/settings/{key}" })
        {
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path, method = "GET", menuId });
        }
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "settings-reader", name = "Settings reader" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        using var grant = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        using var read = await user.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("test-sender", (await read.Content.ReadApiDataAsync()).GetProperty("value").GetString());
        using var list = await user.GetAsync(new Uri("/api/platform/settings/?scope=mail", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mail.sender", Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("key").GetString());
        using var forbiddenWrite = await user.PutAsJsonAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative), new { value = "unauthorized" });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
        using var forbiddenClear = await user.DeleteAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClear.StatusCode);
        using var clear = await root.DeleteAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var cleared = await user.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadApiDataAsync()).GetProperty("value").ValueKind);

        if (disableAccount)
        {
            // 禁用的 HTTP 管理用例另票补齐；此处通过真实仓储安排账号状态，验收仍走设置 HTTP。
            await using var scope = app.Services.CreateAsyncScope();
            var account = await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(new UserId(userId));
            Assert.NotNull(account);
            Assert.True(account.Disable(DateTimeOffset.UtcNow).IsSuccess);
        }
        else
        {
            using var logout = await user.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        using var revoked = await user.GetAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var rootLogout = await root.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, rootLogout.StatusCode);
        using var revokedRoot = await root.PutAsJsonAsync(new Uri("/api/platform/settings/mail.sender", UriKind.Relative), new { value = "revoked" });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRoot.StatusCode);
    }

    [Fact]
    public async Task SignedInUser_WithoutSettingsPermissions_CannotReadOrChangeSettings()
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var created = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "settings-reader", password = "settings-test-password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await LoginAsync(client, "settings-reader", "settings-test-password");

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/platform/settings/mail.sender"),
            (HttpMethod.Get, "/api/platform/settings/?scope=mail"),
            (HttpMethod.Put, "/api/platform/settings/mail.sender"),
            (HttpMethod.Delete, "/api/platform/settings/mail.sender"),
        })
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (method == HttpMethod.Put) { request.Content = JsonContent.Create(new { value = "changed" }); }
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/platform/settings/mail.sender")]
    [InlineData("/api/platform/settings/?scope=mail")]
    public async Task SettingsReads_RejectAnonymousCallers(string path)
    {
        await using var app = new PlatformApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    internal static async Task LoginAsync(HttpClient client, string userName, string password)
    {
        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), new { userName, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadApiDataAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
