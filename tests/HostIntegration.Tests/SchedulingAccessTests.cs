using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingAccessTests
{
    [Fact]
    public async Task ReadGrant_DoesNotGrantScheduleChanges_AndLogoutRevokesTheSession()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        using var anonymous = await user.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var account = await CreateAsync(user, "/api/identity/users", new { userName = "schedule-reader", password = "schedule-test-password" });
        await PlatformSettingsAccessTests.LoginAsync(user, "schedule-reader", "schedule-test-password");
        using var ungranted = await user.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, ungranted.StatusCode);

        var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Read schedules", sortOrder = 1 });
        var menuId = menu.GetProperty("menuId").GetInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/scheduling/tasks", method = "GET", menuId });
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "schedule-reader", name = "Schedule reader" });
        var roleId = role.GetProperty("roleId").GetInt64();
        using var grant = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{account.GetProperty("userId").GetInt64()}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using var allowed = await user.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var historyDenied = await user.GetAsync(new Uri("/api/scheduling/tasks/1/occurrences", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, historyDenied.StatusCode);
        foreach (var path in new[] { "/api/scheduling/tasks/", "/api/scheduling/tasks/1/pause", "/api/scheduling/tasks/1/resume",
            "/api/scheduling/occurrences/11111111-1111-1111-1111-111111111111/retry" })
        {
            using var denied = await user.PostAsJsonAsync(new Uri(path, UriKind.Relative),
                new { code = "unauthorized", intervalSeconds = 60, expectedVersion = 0 });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var logout = await user.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await user.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var rootRead = await root.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, rootRead.StatusCode);
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
