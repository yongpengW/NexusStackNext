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
    public async Task Role_directory_and_rename_keep_separate_permissions_and_survive_a_real_process_restart()
    {
        await using var database = await databases.CreateAsync();
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "lifecycle-root-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        using var files = new JourneyFileStorage();
        Directory.CreateDirectory(files.Root);
        var routes = System.IO.Path.Combine(files.Root, "role-routes.json");
        var table = JsonNode.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            foreach (var destination in cluster!["destinations"]!.AsArray()) { destination!["address"] = platform.Client.BaseAddress.AbsoluteUri; }
        }
        await File.WriteAllTextAsync(routes, table.ToJsonString());
        await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
        var root = gateway.Client;
        using var reader = new HttpClient { BaseAddress = root.BaseAddress };
        using (var anonymous = await reader.GetAsync(Path("/api/identity/roles"))) { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
        await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "lifecycle-root-password");
        var user = await CreateAsync(root, "/api/identity/users", new { userName = "role-directory-reader", password = "role-directory-password" });
        await UserLifecycleHttpTests.LoginAsync(reader, "role-directory-reader", "role-directory-password");
        using (var denied = await reader.GetAsync(Path("/api/identity/roles"))) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "directory-role", name = "Directory %_" })).GetProperty("roleId").ReadHttpInt64();
        var menuId = (await CreateAsync(root, "/api/identity/menus", new { title = "Role directory read", sortOrder = 1 })).GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/roles", method = "GET", menuId });
        using (var grant = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using (var assign = await root.PostAsync(Path($"/api/identity/users/{user.GetProperty("userId").GetString()}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode); }
        foreach (var search in new[] { "DIRECTORY", "%25_" })
        {
            var page = await ReadAsync(reader, "/api/identity/roles?limit=1&search=" + search);
            Assert.Equal(roleId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("roleId").ReadHttpInt64());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextAfterRoleId").ValueKind);
        }
        using (var denied = await reader.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}"), new { expectedVersion = 2, name = "Forbidden" })) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var renamed = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}"), new { expectedVersion = "2", name = "Renamed through gateway" })) { Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode); }
        await platform.CrashAsync();
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "lifecycle-root-password", settings: settings, listenAddress: platform.Client.BaseAddress);
        await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
        var persisted = Assert.Single((await ReadAsync(reader, "/api/identity/roles?search=directory")).GetProperty("items").EnumerateArray());
        Assert.Equal("Renamed through gateway", persisted.GetProperty("name").GetString());
        Assert.Equal(3, persisted.GetProperty("version").ReadHttpInt64());
        var detail = await ReadAsync(root, $"/api/identity/roles/{roleId}");
        Assert.Equal("directory-role", detail.GetProperty("code").GetString());
        Assert.Equal(menuId, Assert.Single(detail.GetProperty("menuIds").EnumerateArray()).ReadHttpInt64());
        using var stale = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}"), new { expectedVersion = "2", name = "Stale after restart" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

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
            await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
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

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(Path(path), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(Path(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
