using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class UserLifecycleGatewayJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Through_the_real_gateway_disable_restart_enable_and_both_password_rotations_keep_old_sessions_revoked()
    {
        await using var database = await databases.CreateAsync();
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "lifecycle-root-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        var routes = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nsn-lifecycle-routes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var table = JsonNode.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
            foreach (var cluster in table["clusters"]!.AsArray())
            {
                foreach (var destination in cluster!["destinations"]!.AsArray()) { destination!["address"] = platform.Client.BaseAddress.AbsoluteUri; }
            }
            await File.WriteAllTextAsync(routes, table.ToJsonString());
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
            var root = gateway.Client;
            using var ordinary = new HttpClient { BaseAddress = root.BaseAddress };
            await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "lifecycle-root-password");
            using var registered = await ordinary.PostAsJsonAsync(Path("/api/identity/users"), new { userName = "gateway-lifecycle", password = "lifecycle-user-password" });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            var id = (await registered.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
            await GrantPlatformReadAsync(root, id);
            var old = await UserLifecycleHttpTests.LoginAsync(ordinary, "gateway-lifecycle", "lifecycle-user-password");
            using var before = await ordinary.GetAsync(Path("/api/platform/audit-capacity"));
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var disabled = await root.PostAsJsonAsync(Path($"/api/identity/users/{id}/disable"), new { expectedVersion = await VersionAsync(root, id) });
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
            await AssertRevokedAsync(ordinary, old);
            await platform.CrashAsync();
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "lifecycle-root-password", settings: settings, listenAddress: platform.Client.BaseAddress);
            using var enabled = await root.PostAsJsonAsync(Path($"/api/identity/users/{id}/enable"), new { expectedVersion = await VersionAsync(root, id) });
            Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
            await AssertRevokedAsync(ordinary, old);
            var fresh = await UserLifecycleHttpTests.LoginAsync(ordinary, "gateway-lifecycle", "lifecycle-user-password");
            using var afterEnable = await ordinary.GetAsync(Path("/api/platform/audit-capacity"));
            Assert.Equal(HttpStatusCode.OK, afterEnable.StatusCode);
            using var rotated = await ordinary.PostAsJsonAsync(Path("/api/identity/me/password"), new
            {
                expectedVersion = await VersionAsync(root, id),
                oldPassword = "lifecycle-user-password",
                newPassword = "self-rotated-password",
                userId = 1,
            });
            Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
            await AssertRevokedAsync(ordinary, fresh);
            using var oldLogin = await ordinary.PostAsJsonAsync(Path("/api/identity/login"), new { userName = "gateway-lifecycle", password = "lifecycle-user-password" });
            Assert.Equal(HttpStatusCode.BadRequest, oldLogin.StatusCode);
            var rotatedTokens = await UserLifecycleHttpTests.LoginAsync(ordinary, "gateway-lifecycle", "self-rotated-password");
            using var reset = await root.PostAsJsonAsync(Path($"/api/identity/users/{id}/password"), new { expectedVersion = await VersionAsync(root, id), newPassword = "administrator-reset-password" });
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            await AssertRevokedAsync(ordinary, rotatedTokens);
            using var replacedLogin = await ordinary.PostAsJsonAsync(Path("/api/identity/login"), new { userName = "gateway-lifecycle", password = "self-rotated-password" });
            Assert.Equal(HttpStatusCode.BadRequest, replacedLogin.StatusCode);
            await UserLifecycleHttpTests.LoginAsync(ordinary, "gateway-lifecycle", "administrator-reset-password");
            using var final = await ordinary.GetAsync(Path("/api/platform/audit-capacity"));
            Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        }
        finally { File.Delete(routes); }
    }

    private static async Task GrantPlatformReadAsync(HttpClient root, long id)
    {
        using var menu = await root.PostAsJsonAsync(Path("/api/identity/menus"), new { title = "Platform read", sortOrder = 1 });
        var menuId = (await menu.Content.ReadApiDataAsync()).GetProperty("menuId").ReadHttpInt64();
        using var resource = await root.PostAsJsonAsync(Path("/api/identity/api-resources"), new { path = "/api/platform/audit-capacity", method = "GET", menuId });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        using var role = await root.PostAsJsonAsync(Path("/api/identity/roles"), new { code = "platform-reader", name = "Platform reader" });
        var roleId = (await role.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
        using var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        using var assigned = await root.PostAsync(Path($"/api/identity/users/{id}/roles/{roleId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
    }

    private static async Task AssertRevokedAsync(HttpClient client, JsonElement old)
    {
        using var denied = await client.GetAsync(Path("/api/platform/audit-capacity"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var refresh = await client.PostAsJsonAsync(Path("/api/identity/refresh"), new { refreshToken = old.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
    }

    private static async Task<long> VersionAsync(HttpClient root, long id)
    {
        using var response = await root.GetAsync(Path($"/api/identity/users/{id}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64();
    }

    private static Uri Path(string value) => new(value, UriKind.Relative);
}
