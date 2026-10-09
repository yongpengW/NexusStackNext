using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class BusinessAuthorizationManagementTests
{
    [Fact]
    public async Task Current_access_rejects_malformed_keys_and_ignores_client_selected_subject_or_allow()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var user = app.CreateClient();
        var id = (await CreateAsync(user, "/api/identity/users", new { userName = "access-subject", password = "access-subject-password" })).GetProperty("userId").GetString();
        await UserLifecycleHttpTests.LoginAsync(user, "access-subject", "access-subject-password");
        foreach (var invalid in new[] { " :GET", "/api/costing/tasks: ", "GET", new string('a', 281) })
        {
            using var rejected = await user.GetAsync(Path("/api/identity/access/v1?permissionKey=" + Uri.EscapeDataString(invalid)));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, Path("/api/identity/access/v1?permissionKey=%2Fapi%2Fcosting%2Ftasks%3AGET&userId=1&allow=true"))
        { Content = JsonContent.Create(new { subject = "1", sessionVersion = "0", permissionKey = "/api/identity/roles:POST", isAllowed = true }) };
        using var response = await user.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = await response.Content.ReadApiDataAsync();
        Assert.Equal(id, decision.GetProperty("subject").GetString());
        Assert.Equal("/api/costing/tasks:GET", decision.GetProperty("permissionKey").GetString());
        Assert.False(decision.GetProperty("isAllowed").GetBoolean());
    }

    [Fact]
    public async Task Missing_role_or_menu_references_are_rejected_without_partially_changing_permissions()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var root = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var userId = (await CreateAsync(root, "/api/identity/users", new { userName = "reference-user", password = "reference-user-password" })).GetProperty("userId").GetString();
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "reference-role", name = "Reference role" })).GetProperty("roleId").GetString();
        using (var missingRole = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/90001"), null)) { Assert.Equal(HttpStatusCode.NotFound, missingRole.StatusCode); }
        using (var missingMenu = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/90002"), null)) { Assert.Equal(HttpStatusCode.NotFound, missingMenu.StatusCode); }
        using (var missingBinding = await root.PostAsJsonAsync(Path("/api/identity/api-resources"), new { path = "/api/costing/tasks", method = "GET", menuId = "90002" })) { Assert.Equal(HttpStatusCode.NotFound, missingBinding.StatusCode); }
        using (var missingReplacement = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = "1", menuIds = new[] { "90002" } })) { Assert.Equal(HttpStatusCode.NotFound, missingReplacement.StatusCode); }
        var currentUser = await ReadAsync(root, $"/api/identity/users/{userId}");
        Assert.Equal(1, currentUser.GetProperty("version").ReadHttpInt64());
        Assert.Empty(currentUser.GetProperty("roleIds").EnumerateArray());
        var currentRole = await ReadAsync(root, $"/api/identity/roles/{roleId}");
        Assert.Equal(1, currentRole.GetProperty("version").ReadHttpInt64());
        Assert.Empty(currentRole.GetProperty("menuIds").EnumerateArray());
    }

    [Fact]
    public async Task Conditional_role_and_menu_revocation_changes_only_the_target_aggregate_and_preserves_the_same_session()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var menuId = (await CreateAsync(root, "/api/identity/menus", new { title = "Reader", sortOrder = 1 })).GetProperty("menuId").GetString();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus", method = "GET", menuId });
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "conditional-reader", name = "Reader" })).GetProperty("roleId").GetString();
        var userId = (await CreateAsync(user, "/api/identity/users", new { userName = "conditional-user", password = "conditional-user-password" })).GetProperty("userId").GetString();
        await UserLifecycleHttpTests.LoginAsync(user, "conditional-user", "conditional-user-password");
        using (var grant = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using (var assign = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode); }
        using (var before = await user.GetAsync(Path("/api/identity/menus"))) { Assert.Equal(HttpStatusCode.OK, before.StatusCode); }
        var roleBefore = await ReadAsync(root, $"/api/identity/roles/{roleId}");
        Assert.Equal(2, roleBefore.GetProperty("version").ReadHttpInt64());
        using (var forbiddenManagement = await user.GetAsync(Path($"/api/identity/roles/{roleId}"))) { Assert.Equal(HttpStatusCode.Forbidden, forbiddenManagement.StatusCode); }
        using (var missingVersion = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { menuIds = Array.Empty<string>() })) { Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode); }
        using (var replace = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = "2", menuIds = Array.Empty<string>() })) { Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode); }
        using (var denied = await user.GetAsync(Path("/api/identity/menus"))) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var noOp = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = "3", menuIds = Array.Empty<string>() })) { Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode); }
        Assert.Equal(3, (await ReadAsync(root, $"/api/identity/roles/{roleId}")).GetProperty("version").ReadHttpInt64());
        using (var stale = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = "2", menuIds = new[] { menuId } })) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var restore = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode); }
        using (var restored = await user.GetAsync(Path("/api/identity/menus"))) { Assert.Equal(HttpStatusCode.OK, restored.StatusCode); }
        var userVersion = (await ReadAsync(root, $"/api/identity/users/{userId}")).GetProperty("version").ReadHttpInt64();
        using (var revoke = await root.PostAsJsonAsync(Path($"/api/identity/users/{userId}/roles/{roleId}/revoke"), new { expectedVersion = userVersion })) { Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode); }
        using (var denied = await user.GetAsync(Path("/api/identity/menus"))) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var stillAuthenticated = await user.GetAsync(Path("/api/identity/me"))) { Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode); }
        using (var noOp = await root.PostAsJsonAsync(Path($"/api/identity/users/{userId}/roles/{roleId}/revoke"), new { expectedVersion = userVersion + 1 })) { Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode); }
        Assert.Equal(userVersion + 1, (await ReadAsync(root, $"/api/identity/users/{userId}")).GetProperty("version").ReadHttpInt64());
        using (var restore = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode); }
        using (var stale = await root.PostAsJsonAsync(Path($"/api/identity/users/{userId}/roles/{roleId}/revoke"), new { expectedVersion = userVersion + 1 })) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using var final = await user.GetAsync(Path("/api/identity/menus"));
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
    }

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

    private static Uri Path(string path) => new(path, UriKind.Relative);
}
