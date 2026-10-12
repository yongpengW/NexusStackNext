using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class RoleDeletionGatewayJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Logical_role_delete_removes_authority_keeps_user_reference_and_survives_restart()
    {
        await using var database = await databases.CreateAsync();
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "role-delete-root-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        using var files = new JourneyFileStorage();
        Directory.CreateDirectory(files.Root);
        var routes = Path.Combine(files.Root, "role-delete-routes.json");
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            foreach (var destination in cluster!["destinations"]!.AsArray()) { destination!["address"] = platform.Client.BaseAddress.AbsoluteUri; }
        }
        await File.WriteAllTextAsync(routes, table.ToJsonString());

        await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
        using var root = gateway.Client;
        using var ordinary = new HttpClient { BaseAddress = root.BaseAddress };
        await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "role-delete-root-password");
        using var registered = await root.PostAsJsonAsync(PathOf("/api/identity/users"), new
        {
            userName = "role-delete-user",
            password = "role-delete-user-password",
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var userId = (await registered.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();

        using var menuResponse = await root.PostAsJsonAsync(PathOf("/api/identity/menus"), new { title = "Role delete read", sortOrder = 1 });
        var menuId = (await menuResponse.Content.ReadApiDataAsync()).GetProperty("menuId").ReadHttpInt64();
        using var resource = await root.PostAsJsonAsync(PathOf("/api/identity/api-resources"), new
        {
            path = "/api/platform/audit-capacity",
            method = "GET",
            menuId,
        });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        using var roleResponse = await root.PostAsJsonAsync(PathOf("/api/identity/roles"), new
        {
            code = "role-delete-stable-code",
            name = "Role delete",
        });
        var roleId = (await roleResponse.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
        using (var grant = await root.PostAsync(PathOf($"/api/identity/roles/{roleId}/menus/{menuId}"), null))
        { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using (var assign = await root.PostAsync(PathOf($"/api/identity/users/{userId}/roles/{roleId}"), null))
        { Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode); }
        await UserLifecycleHttpTests.LoginAsync(ordinary, "role-delete-user", "role-delete-user-password");
        using (var allowed = await ordinary.GetAsync(PathOf("/api/platform/audit-capacity")))
        { Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }

        var directory = await ReadAsync(root, $"/api/identity/roles?search=stable-code");
        Assert.Equal(roleId, Assert.Single(directory.GetProperty("items").EnumerateArray()).GetProperty("roleId").ReadHttpInt64());
        using (var stale = new HttpRequestMessage(HttpMethod.Delete, PathOf($"/api/identity/roles/{roleId}"))
        { Content = JsonContent.Create(new { expectedVersion = 1 }) })
        using (var response = await root.SendAsync(stale))
        { Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); }

        using (var deleted = new HttpRequestMessage(HttpMethod.Delete, PathOf($"/api/identity/roles/{roleId}"))
        { Content = JsonContent.Create(new { expectedVersion = 2 }) })
        using (var response = await root.SendAsync(deleted))
        { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
        using (var denied = await ordinary.GetAsync(PathOf("/api/platform/audit-capacity")))
        { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var afterDelete = await ReadAsync(root, "/api/identity/roles?search=stable-code");
        Assert.Empty(afterDelete.GetProperty("items").EnumerateArray());
        using (var detail = await root.GetAsync(PathOf($"/api/identity/roles/{roleId}")))
        { Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode); }
        var user = await ReadAsync(root, $"/api/identity/users/{userId}");
        Assert.Equal(roleId, Assert.Single(user.GetProperty("roleIds").EnumerateArray()).ReadHttpInt64());
        using (var reused = await root.PostAsJsonAsync(PathOf("/api/identity/roles"), new
        {
            code = "role-delete-stable-code",
            name = "Reused code",
        }))
        { Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode); }

        await platform.CrashAsync();
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "role-delete-root-password", settings: settings,
            listenAddress: platform.Client.BaseAddress);
        await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
        using var afterRestart = await ordinary.GetAsync(PathOf("/api/platform/audit-capacity"));
        Assert.Equal(HttpStatusCode.Forbidden, afterRestart.StatusCode);
        using var missingAfterRestart = await root.GetAsync(PathOf($"/api/identity/roles/{roleId}"));
        Assert.Equal(HttpStatusCode.NotFound, missingAfterRestart.StatusCode);
        var retained = await ReadAsync(root, $"/api/identity/users/{userId}");
        Assert.Equal(roleId, Assert.Single(retained.GetProperty("roleIds").EnumerateArray()).ReadHttpInt64());
    }

    private static Uri PathOf(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(PathOf(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
