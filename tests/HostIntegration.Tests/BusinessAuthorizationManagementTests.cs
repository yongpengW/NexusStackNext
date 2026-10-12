using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class BusinessAuthorizationManagementTests
{
    [Fact]
    public async Task Role_directory_pages_and_searches_without_exposing_management_to_an_ordinary_user()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var first = (await CreateAsync(root, "/api/identity/roles", new { code = "directory-first", name = "Directory one" })).GetProperty("roleId").GetString();
        var second = (await CreateAsync(root, "/api/identity/roles", new { code = "directory-second", name = "Directory two %_" })).GetProperty("roleId").GetString();
        _ = await CreateAsync(root, "/api/identity/roles", new { code = "unrelated", name = "Other" });
        var page = await ReadAsync(root, "/api/identity/roles?search=DIRECTORY&limit=1");
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(first, row.GetProperty("roleId").GetString());
        Assert.Equal("directory-first", row.GetProperty("code").GetString());
        Assert.Equal("Directory one", row.GetProperty("name").GetString());
        Assert.Equal(1, row.GetProperty("version").ReadHttpInt64());
        Assert.Equal(first, page.GetProperty("nextAfterRoleId").GetString());
        var last = await ReadAsync(root, $"/api/identity/roles?search=directory&limit=1&afterRoleId={first}");
        Assert.Equal(second, Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("roleId").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextAfterRoleId").ValueKind);
        var literal = await ReadAsync(root, "/api/identity/roles?search=%25_");
        Assert.Equal(second, Assert.Single(literal.GetProperty("items").EnumerateArray()).GetProperty("roleId").GetString());
        foreach (var invalid in new[] { "limit=0", "limit=101", "afterRoleId=-1", "search=" + new string('a', 65) })
        {
            using var rejected = await root.GetAsync(Path("/api/identity/roles?" + invalid));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        _ = await CreateAsync(user, "/api/identity/users", new { userName = "directory-user", password = "directory-user-password" });
        await UserLifecycleHttpTests.LoginAsync(user, "directory-user", "directory-user-password");
        using var denied = await user.GetAsync(Path("/api/identity/roles"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Conditional_role_rename_preserves_code_grants_and_version_on_no_op_or_rejection()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var root = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var menu = (await CreateAsync(root, "/api/identity/menus", new { title = "Role menu", sortOrder = 1 })).GetProperty("menuId").GetString();
        var role = (await CreateAsync(root, "/api/identity/roles", new { code = "rename-role", name = "Original" })).GetProperty("roleId").GetString();
        using (var grant = await root.PostAsync(Path($"/api/identity/roles/{role}/menus/{menu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using (var rename = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { expectedVersion = "2", name = " Renamed " })) { Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode); }
        var current = await ReadAsync(root, $"/api/identity/roles/{role}");
        Assert.Equal("Renamed", current.GetProperty("name").GetString());
        Assert.Equal("rename-role", current.GetProperty("code").GetString());
        Assert.Equal(menu, Assert.Single(current.GetProperty("menuIds").EnumerateArray()).GetString());
        Assert.Equal(3, current.GetProperty("version").ReadHttpInt64());
        var facts = (await ReadAsync(root, "/api/identity/audit-capacity")).GetProperty("retainedRecords").ReadHttpInt64();
        using (var noOp = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { expectedVersion = 3, name = "Renamed" })) { Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode); }
        using (var stale = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { expectedVersion = 2, name = "Stale" })) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var missing = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { name = "Missing version" })) { Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode); }
        using (var invalid = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { expectedVersion = 0, name = "Invalid version" })) { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var invalid = await root.PutAsJsonAsync(Path($"/api/identity/roles/{role}"), new { expectedVersion = 3, name = " " })) { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var absent = await root.PutAsJsonAsync(Path("/api/identity/roles/999999"), new { expectedVersion = 1, name = "Absent" })) { Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode); }
        Assert.Equal(3, (await ReadAsync(root, $"/api/identity/roles/{role}")).GetProperty("version").ReadHttpInt64());
        Assert.Equal(facts, (await ReadAsync(root, "/api/identity/audit-capacity")).GetProperty("retainedRecords").ReadHttpInt64());
    }

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
