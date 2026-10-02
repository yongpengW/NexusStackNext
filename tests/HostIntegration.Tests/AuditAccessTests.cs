using System.Net;
using System.Net.Http.Json;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class AuditAccessTests
{
    [Theory]
    [InlineData("routes.json")]
    [InlineData("routes.pricing.json")]
    [InlineData("routes.business.json")]
    public async Task ShippedGatewayRoutes_ExposeAuthorizedInvestigationOnly(string file)
    {
        await using var platform = new PlatformAppWithRootAccount();
        platform.UseKestrel(0);
        using var direct = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(direct.BaseAddress!.AbsoluteUri)
        {
            SigningKey = "integration-test-signing-key-long-enough-for-hs256",
            RateLimitPermitLimit = 20,
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend" }));
        using var client = gateway.CreateClient();
        using var anonymous = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousOperations = await client.GetAsync(new Uri("/api/auditing/operations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousOperations.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(direct, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        client.DefaultRequestHeaders.Authorization = direct.DefaultRequestHeaders.Authorization;
        using var allowed = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var operations = await client.GetAsync(new Uri("/api/auditing/operations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
        using var deliveries = await client.GetAsync(new Uri("/api/platform/audit-deliveries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, deliveries.StatusCode);
        using var retry = await client.PostAsJsonAsync(new Uri($"/api/platform/audit-deliveries/{Guid.NewGuid()}/retry", UriKind.Relative),
            new { expectedDeadLetteredAt = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        using var rejected = await client.PostAsJsonAsync(new Uri("/api/auditing/entries", UriKind.Relative), new { actorId = "forged" });
        Assert.Contains(rejected.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task InvestigatorGrant_DoesNotGrantDeliveryRetry_AndLogoutRevokesExistingToken()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var created = await CreateAsync(user, "/api/identity/users", new { userName = "investigator", password = "investigator-password" });
        var userId = created.GetProperty("userId").ReadHttpInt64();
        await PlatformSettingsAccessTests.LoginAsync(user, "investigator", "investigator-password");
        using var forbiddenOperations = await user.GetAsync(new Uri("/api/auditing/operations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenOperations.StatusCode);
        var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Audit investigation", sortOrder = 1 });
        var menuId = menu.GetProperty("menuId").ReadHttpInt64();
        foreach (var path in new[] { "/api/auditing/entries", "/api/auditing/operations", "/api/platform/audit-deliveries" })
        {
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path, method = "GET", menuId });
        }
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "audit-reader", name = "Audit reader" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        using var grant = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        foreach (var path in new[] { "/api/auditing/entries", "/api/auditing/operations", "/api/platform/audit-deliveries" })
        {
            using var read = await user.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        using var retry = await user.PostAsJsonAsync(new Uri($"/api/platform/audit-deliveries/{Guid.NewGuid()}/retry", UriKind.Relative),
            new { expectedDeadLetteredAt = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        using var logout = await user.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await user.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var revokedOperations = await user.GetAsync(new Uri("/api/auditing/operations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, revokedOperations.StatusCode);
    }

    private static async Task<System.Text.Json.JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    [Fact]
    public async Task Investigation_RequiresPermission_AndBoundsPagination()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        using var anonymous = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "audit-reader", password = "audit-reader-password" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(client, "audit-reader", "audit-reader-password");
        using var forbidden = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var allowed = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        foreach (var query in new[] { "page=0", "page=1001", "limit=0", "limit=101" })
        {
            using var invalid = await client.GetAsync(new Uri("/api/auditing/entries?" + query, UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var invalidOperations = await client.GetAsync(new Uri("/api/auditing/operations?" + query, UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, invalidOperations.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/auditing/entries")]
    [InlineData("/api/auditing/operations")]
    public async Task HttpClients_CannotSubmitAuditRecords_EvenWithRootSession(string path)
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        var forged = new { messageId = Guid.NewGuid(), action = "platform.setting.changed", subjectType = "setting", subjectId = "1", actorId = "victim" };
        using var anonymous = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), forged);
        Assert.Contains(anonymous.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var root = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), forged);
        Assert.Contains(root.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
}
