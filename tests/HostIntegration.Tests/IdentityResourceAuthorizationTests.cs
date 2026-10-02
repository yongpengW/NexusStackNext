using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityResourceAuthorizationTests
{
    [Fact]
    public async Task MenuReader_CannotAttachAnUnregisteredManagementPermissionToTheirMenu()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var reader = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var (menuId, userId) = await CreateMenuReaderAsync(root, reader);
        await AssertCanReadMenuAsync(reader, menuId);
        await AssertCannotCreateRoleAsync(reader);
        var before = await ReadPermissionsAsync(root, userId);

        using var escalation = await reader.PostAsJsonAsync(Relative("/api/identity/api-resources"),
            new { path = "/api/identity/roles", method = "POST", menuId });
        Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
        await AssertCannotCreateRoleAsync(reader);
        Assert.Equal(before, await ReadPermissionsAsync(root, userId));
    }

    [Fact]
    public async Task ExplicitResourceManager_CanRegisterPermissions_WhileAnonymousCallsAreRejected()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var manager = app.CreateClient();
        using var anonymous = await manager.PostAsJsonAsync(Relative("/api/identity/api-resources"),
            new { path = "/api/identity/roles", method = "POST", menuId = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var (menuId, userId) = await CreateMenuReaderAsync(root, manager);
        await AssertCanReadMenuAsync(manager, menuId);

        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/api-resources", method = "POST", menuId });
        var resource = await CreateAsync(manager, "/api/identity/api-resources",
            new { path = "/api/identity/users/{userId}/permissions", method = "GET", menuId });
        Assert.Contains(resource.GetProperty("permissionKey").GetString(), await ReadPermissionsAsync(manager, userId));
        await AssertCannotCreateRoleAsync(manager);
    }

    [PostgresFact]
    public async Task Gateway_RejectsResourceEscalation_AfterIndependentMigrationAndProcessRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var routes = Path.Combine(Path.GetTempPath(), $"nsn-identity-resource-routes-{Guid.NewGuid():N}.json");
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        string? menuId = null;
        string? userId = null;
        string[]? acceptedPermissions = null;
        try
        {
            for (var iteration = 0; iteration < 2; iteration++)
            {
                await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "resource-root-test-password", settings: settings);
                await WritePlatformRoutesAsync(routes, platform.Client.BaseAddress!);
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes);
                using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
                using var reader = new HttpClient { BaseAddress = gateway.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
                using var anonymous = await reader.PostAsJsonAsync(Relative("/api/identity/api-resources"),
                    new { path = "/api/identity/roles", method = "POST", menuId });
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "resource-root-test-password");
                if (iteration == 0)
                {
                    (menuId, userId) = await CreateMenuReaderAsync(root, reader);
                    acceptedPermissions = await ReadPermissionsAsync(root, userId);
                }
                else { await PlatformSettingsAccessTests.LoginAsync(reader, "resource-reader", "resource-reader-test-password"); }
                Assert.NotNull(menuId);
                Assert.NotNull(userId);
                await AssertCanReadMenuAsync(reader, menuId);
                await AssertCannotCreateRoleAsync(reader);
                using var denied = await reader.PostAsJsonAsync(Relative("/api/identity/api-resources"),
                    new { path = "/api/identity/roles", method = "POST", menuId });
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                await AssertCannotCreateRoleAsync(reader);
                Assert.Equal(acceptedPermissions, await ReadPermissionsAsync(root, userId));
                if (iteration == 1)
                {
                    // PostgreSQL 的路由唯一约束证明两次被拒请求均未留下资源；不授给读者。
                    _ = await CreateAsync(root, "/api/identity/api-resources",
                        new { path = "/api/identity/roles", method = "POST", menuId = (string?)null });
                    await AssertCannotCreateRoleAsync(reader);
                }
            }
        }
        finally { File.Delete(routes); }
    }

    private static async Task WritePlatformRoutesAsync(string path, Uri platform)
    {
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json")))!;
        var cluster = Assert.Single(table["clusters"]!.AsArray());
        Assert.Equal("platform-host", cluster!["clusterId"]!.GetValue<string>());
        var destinations = cluster["destinations"]!.AsArray();
        Assert.NotEmpty(destinations);
        foreach (var destination in destinations) { destination!["address"] = platform.ToString(); }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static async Task<(string MenuId, string UserId)> CreateMenuReaderAsync(HttpClient root, HttpClient reader)
    {
        var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Resource reader", sortOrder = 1 });
        var menuId = menu.GetProperty("menuId").GetString()!;
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus", method = "GET", menuId });
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "resource-reader", name = "Resource reader" });
        var roleId = role.GetProperty("roleId").GetString();
        using var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{menuId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        var user = await CreateAsync(reader, "/api/identity/users", new { userName = "resource-reader", password = "resource-reader-test-password" });
        var userId = user.GetProperty("userId").GetString()!;
        using var assigned = await root.PostAsync(Relative($"/api/identity/users/{userId}/roles/{roleId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(reader, "resource-reader", "resource-reader-test-password");
        return (menuId, userId);
    }

    private static async Task AssertCanReadMenuAsync(HttpClient reader, string menuId)
    {
        using var menus = await reader.GetAsync(Relative("/api/identity/menus"));
        Assert.Equal(HttpStatusCode.OK, menus.StatusCode);
        var body = await menus.Content.ReadApiDataAsync();
        Assert.Contains(body.GetProperty("items").EnumerateArray(), entry => entry.GetProperty("menuId").GetString() == menuId);
    }

    private static async Task AssertCannotCreateRoleAsync(HttpClient reader)
    {
        using var denied = await reader.PostAsJsonAsync(Relative("/api/identity/roles"), new { code = "unauthorized", name = "Unauthorized" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static async Task<string[]> ReadPermissionsAsync(HttpClient root, string userId)
    {
        using var response = await root.GetAsync(Relative($"/api/identity/users/{userId}/permissions"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadApiDataAsync()).GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(Relative(path), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
