using System.Globalization;
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
    public async Task MenuEditor_ChangesContentConditionally_WithoutChangingPermissionOrNoOpVersion()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var editor = app.CreateClient();
        using var anonymous = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var (menuId, userId) = await CreateMenuReaderAsync(root, editor);
        var before = await ReadMenusAsync(root);
        var version = before.GetProperty("version").ReadHttpInt64();
        var path = Relative($"/api/identity/menus/{menuId}");
        var edit = new { title = "Renamed", sortOrder = 7, expectedVersion = version };
        using (var unauthenticated = await anonymous.PutAsJsonAsync(path, edit))
        { Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode); }
        using (var forbidden = await editor.PutAsJsonAsync(path, edit))
        { Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode); }

        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus/{menuId}", method = "PUT", menuId });
        var permissions = await ReadPermissionsAsync(root, userId);
        using (var changed = await editor.PutAsJsonAsync(path, edit))
        { Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); }
        var current = await ReadMenusAsync(editor);
        Assert.Equal(version + 1, current.GetProperty("version").ReadHttpInt64());
        var node = Assert.Single(current.GetProperty("items").EnumerateArray());
        Assert.Equal(menuId, node.GetProperty("menuId").GetString());
        Assert.Equal("Renamed", node.GetProperty("title").GetString());
        Assert.Equal(7, node.GetProperty("sortOrder").GetInt32());
        using (var stale = await editor.PutAsJsonAsync(path, edit))
        { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var noOp = await editor.PutAsJsonAsync(path, new { title = "Renamed", sortOrder = 7, expectedVersion = version + 1 }))
        { Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode); }
        using (var invalid = await editor.PutAsJsonAsync(path, new { title = " ", sortOrder = 7, expectedVersion = version + 1 }))
        { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var invalidVersion = await editor.PutAsJsonAsync(path, new { title = "Renamed", sortOrder = 7, expectedVersion = 0 }))
        { Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode); }
        using (var missing = await root.PutAsJsonAsync(Relative("/api/identity/menus/1"), edit))
        { Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); }
        Assert.Equal(current.GetRawText(), (await ReadMenusAsync(editor)).GetRawText());
        Assert.Equal(permissions, await ReadPermissionsAsync(root, userId));
    }

    [Fact]
    public async Task MenuMover_RebasesTheWholeSubtree_AndRejectsCyclesAndStaleVersions()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var first = await CreateAsync(root, "/api/identity/menus", new { title = "First", sortOrder = 1 });
        var firstId = first.GetProperty("menuId").GetString()!;
        var second = await CreateAsync(root, "/api/identity/menus", new { title = "Second", sortOrder = 2 });
        var secondId = second.GetProperty("menuId").GetString()!;
        var child = await CreateAsync(root, "/api/identity/menus", new { title = "Child", sortOrder = 1, parentMenuId = long.Parse(firstId, CultureInfo.InvariantCulture) });
        var childId = child.GetProperty("menuId").GetString()!;
        var grandchild = await CreateAsync(root, "/api/identity/menus", new { title = "Grandchild", sortOrder = 1, parentMenuId = long.Parse(childId, CultureInfo.InvariantCulture) });
        var grandchildId = grandchild.GetProperty("menuId").GetString()!;
        var before = await ReadMenusAsync(root);
        var version = before.GetProperty("version").ReadHttpInt64();
        var movePath = Relative($"/api/identity/menus/{childId}/move");
        using (var moved = await root.PostAsJsonAsync(movePath, new { expectedVersion = version, newParentMenuId = long.Parse(secondId, CultureInfo.InvariantCulture) }))
        { Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode); }
        var after = await ReadMenusAsync(root);
        var movedChild = Assert.Single(after.GetProperty("items").EnumerateArray(), item => item.GetProperty("menuId").GetString() == childId);
        var movedGrandchild = Assert.Single(after.GetProperty("items").EnumerateArray(), item => item.GetProperty("menuId").GetString() == grandchildId);
        Assert.Equal(secondId, movedChild.GetProperty("parentMenuId").GetString());
        Assert.Equal($"/{secondId}/{childId}/", movedChild.GetProperty("path").GetString());
        Assert.Equal(childId, movedGrandchild.GetProperty("parentMenuId").GetString());
        Assert.Equal($"/{secondId}/{childId}/{grandchildId}/", movedGrandchild.GetProperty("path").GetString());
        var movedVersion = after.GetProperty("version").ReadHttpInt64();
        using (var noOp = await root.PostAsJsonAsync(movePath, new { expectedVersion = movedVersion, newParentMenuId = long.Parse(secondId, CultureInfo.InvariantCulture) }))
        { Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode); }
        Assert.Equal(movedVersion, (await ReadMenusAsync(root)).GetProperty("version").ReadHttpInt64());
        using (var cycle = await root.PostAsJsonAsync(movePath, new { expectedVersion = movedVersion, newParentMenuId = long.Parse(grandchildId, CultureInfo.InvariantCulture) }))
        { Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode); }
        using (var missingParent = await root.PostAsJsonAsync(movePath, new { expectedVersion = movedVersion, newParentMenuId = 999999L }))
        { Assert.Equal(HttpStatusCode.NotFound, missingParent.StatusCode); }
        using (var stale = await root.PostAsJsonAsync(movePath, new { expectedVersion = version, newParentMenuId = (long?)null }))
        { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        Assert.Equal(movedVersion, (await ReadMenusAsync(root)).GetProperty("version").ReadHttpInt64());
    }

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

    [Fact]
    public async Task RemovingAMenu_StopsItsHistoricalEndpointPermission_AndRejectsParentRemoval()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var reader = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);

        var parent = await CreateAsync(root, "/api/identity/menus", new { title = "Parent", sortOrder = 1 });
        var parentId = parent.GetProperty("menuId").ReadHttpInt64();
        var child = await CreateAsync(root, "/api/identity/menus", new { title = "Child", sortOrder = 1, parentMenuId = parentId });
        var childId = child.GetProperty("menuId").ReadHttpInt64();
        var leaf = await CreateAsync(root, "/api/identity/menus", new { title = "Reader", sortOrder = 2 });
        var leafId = leaf.GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus", method = "GET", menuId = leafId });
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "menu-delete-reader", name = "Menu delete reader" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        using (var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{leafId}"), null))
        { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        var user = await CreateAsync(reader, "/api/identity/users", new { userName = "menu-delete-reader", password = "menu-delete-reader-password" });
        var userId = user.GetProperty("userId").ReadHttpInt64();
        using (var assigned = await root.PostAsync(Relative($"/api/identity/users/{userId}/roles/{roleId}"), null))
        { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
        await PlatformSettingsAccessTests.LoginAsync(reader, "menu-delete-reader", "menu-delete-reader-password");

        using (var before = await reader.GetAsync(Relative("/api/identity/menus")))
        { Assert.Equal(HttpStatusCode.OK, before.StatusCode); }

        var current = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
        var version = current.GetProperty("data").GetProperty("version").ReadHttpInt64();
        using (var parentDelete = await root.PostAsJsonAsync(Relative($"/api/identity/menus/{parentId}/remove"), new { expectedVersion = version }))
        { Assert.Equal(HttpStatusCode.Conflict, parentDelete.StatusCode); }

        current = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
        version = current.GetProperty("data").GetProperty("version").ReadHttpInt64();
        using (var removed = await root.PostAsJsonAsync(Relative($"/api/identity/menus/{leafId}/remove"), new { expectedVersion = version }))
        { Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); }
        using (var denied = await reader.GetAsync(Relative("/api/identity/menus")))
        { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var childStillThere = await root.GetAsync(Relative("/api/identity/menus")))
        { Assert.Equal(HttpStatusCode.OK, childStillThere.StatusCode); }
        _ = childId;
    }

    [PostgresFact]
    public async Task Gateway_RejectsResourceEscalation_AfterIndependentMigrationAndProcessRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
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

    [PostgresFact]
    public async Task Gateway_StopsAReaderAfterItsGrantedMenuIsDeleted()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        var routes = Path.Combine(Path.GetTempPath(), $"nsn-identity-menu-delete-routes-{Guid.NewGuid():N}.json");
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        try
        {
            await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "menu-delete-root-password", settings: settings);
            await WritePlatformRoutesAsync(routes, platform.Client.BaseAddress!);
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
            using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            using var reader = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "menu-delete-root-password");
            var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Gateway delete", sortOrder = 1 });
            var menuId = menu.GetProperty("menuId").GetString()!;
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus", method = "GET", menuId });
            var role = await CreateAsync(root, "/api/identity/roles", new { code = "gateway-menu-reader", name = "Gateway menu reader" });
            var roleId = role.GetProperty("roleId").GetString()!;
            using (var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{menuId}"), null))
            { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
            var user = await CreateAsync(reader, "/api/identity/users", new { userName = "gateway-menu-reader", password = "gateway-menu-reader-password" });
            var userId = user.GetProperty("userId").GetString()!;
            using (var assigned = await root.PostAsync(Relative($"/api/identity/users/{userId}/roles/{roleId}"), null))
            { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
            await PlatformSettingsAccessTests.LoginAsync(reader, "gateway-menu-reader", "gateway-menu-reader-password");
            using (var allowed = await reader.GetAsync(Relative("/api/identity/menus")))
            { Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }
            var menus = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
            var version = menus.GetProperty("data").GetProperty("version").ReadHttpInt64();
            using (var removed = await root.PostAsJsonAsync(Relative($"/api/identity/menus/{menuId}/remove"), new { expectedVersion = version }))
            { Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); }
            using var denied = await reader.GetAsync(Relative("/api/identity/menus"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally { File.Delete(routes); }
    }

    [PostgresFact]
    public async Task Gateway_UpdatesAndMovesMenusWithTheObservedTreeVersion()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        var routes = Path.Combine(Path.GetTempPath(), $"nsn-identity-menu-edit-routes-{Guid.NewGuid():N}.json");
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        try
        {
            await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "menu-edit-gateway-password", settings: settings);
            await WritePlatformRoutesAsync(routes, platform.Client.BaseAddress!);
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
            using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "menu-edit-gateway-password");
            var first = await CreateAsync(root, "/api/identity/menus", new { title = "First", sortOrder = 1 });
            var firstId = first.GetProperty("menuId").ReadHttpInt64();
            var second = await CreateAsync(root, "/api/identity/menus", new { title = "Second", sortOrder = 2 });
            var secondId = second.GetProperty("menuId").ReadHttpInt64();
            var child = await CreateAsync(root, "/api/identity/menus", new { title = "Child", sortOrder = 1, parentMenuId = firstId });
            var childId = child.GetProperty("menuId").ReadHttpInt64();
            var before = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
            var version = before.GetProperty("data").GetProperty("version").ReadHttpInt64();
            using (var updated = await root.PutAsJsonAsync(Relative($"/api/identity/menus/{childId}"),
                new { title = "Gateway child", sortOrder = 9, expectedVersion = version }))
            { Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode); }
            var afterUpdate = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
            version = afterUpdate.GetProperty("data").GetProperty("version").ReadHttpInt64();
            using (var moved = await root.PostAsJsonAsync(Relative($"/api/identity/menus/{childId}/move"),
                new { expectedVersion = version, newParentMenuId = secondId }))
            { Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode); }
            var afterMove = await root.GetFromJsonAsync<JsonElement>(Relative("/api/identity/menus"));
            var movedChild = Assert.Single(afterMove.GetProperty("data").GetProperty("items").EnumerateArray(), item => item.GetProperty("menuId").ReadHttpInt64() == childId);
            Assert.Equal(secondId, movedChild.GetProperty("parentMenuId").ReadHttpInt64());
            Assert.Equal("Gateway child", movedChild.GetProperty("title").GetString());
            Assert.Equal(9, movedChild.GetProperty("sortOrder").GetInt32());
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

    private static async Task<JsonElement> ReadMenusAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Relative("/api/identity/menus"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
