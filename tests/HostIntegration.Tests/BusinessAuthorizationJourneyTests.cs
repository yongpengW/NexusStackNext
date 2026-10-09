using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class BusinessAuthorizationJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Ordinary_user_with_cost_read_permission_can_read_with_the_same_token_without_pricing_or_write_access()
    {
        await using var identity = await databases.CreateAsync();
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(identity.ConnectionString, "business-root-test-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: settings);
        await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString, settings: settings);
        var routes = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nsn-business-access-routes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await WriteRoutesAsync(routes, platform.Client.BaseAddress, costing.Client.BaseAddress!, pricing.Client.BaseAddress!);
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
            using var root = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            using var user = new HttpClient { BaseAddress = gateway.Client.BaseAddress };
            using (var anonymous = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
            var registered = await CreateAsync(user, "/api/identity/users", new { userName = "business-reader", password = "business-reader-test-password" });
            var userId = registered.GetProperty("userId").GetString();
            await UserLifecycleHttpTests.LoginAsync(user, "business-reader", "business-reader-test-password");
            await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "business-root-test-password");
            using (var denied = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
            var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Cost read", sortOrder = 1 });
            var menuId = menu.GetProperty("menuId").GetString();
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/costing/tasks", method = "GET", menuId });
            var role = await CreateAsync(root, "/api/identity/roles", new { code = "cost-reader", name = "Cost reader" });
            var roleId = role.GetProperty("roleId").GetString();
            using (var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
            using (var assigned = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
            using (var read = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.OK, read.StatusCode); }
            costing.Client.DefaultRequestHeaders.Authorization = user.DefaultRequestHeaders.Authorization;
            using (var direct = await costing.Client.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.OK, direct.StatusCode); }
            using (var pricingDenied = await user.GetAsync(Path("/api/pricing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, pricingDenied.StatusCode); }
            var taskId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var input = new { requestId = taskId, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m, permissionKey = "/api/costing/tasks:GET", allow = true };
            using (var write = await user.PostAsJsonAsync(Path("/api/costing/cost"), input)) { Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode); }
            var writeMenu = await CreateAsync(root, "/api/identity/menus", new { title = "Cost submission", sortOrder = 2 });
            var writeMenuId = writeMenu.GetProperty("menuId").GetString();
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/costing/cost", method = "POST", menuId = writeMenuId });
            using (var grantWrite = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{writeMenuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grantWrite.StatusCode); }
            using (var accepted = await user.PostAsJsonAsync(Path("/api/costing/cost"), input)) { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
            // Submission does not implicitly grant task detail, cancellation, retry, batch or fact-delivery operations.
            foreach (var path in new[] { $"/api/costing/tasks/{taskId}", "/api/costing/batches", "/api/costing/audit-deliveries" })
            { using var denied = await user.GetAsync(Path(path)); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
            foreach (var suffix in new[] { "cancel", "retry" })
            { using var denied = await user.PostAsJsonAsync(Path($"/api/costing/tasks/{taskId}/{suffix}"), new { expectedEpoch = "0" }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
            using (var roleState = await root.GetAsync(Path($"/api/identity/roles/{roleId}")))
            {
                var version = (await roleState.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64();
                using var withdraw = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = version, menuIds = Array.Empty<string>() });
                Assert.Equal(HttpStatusCode.NoContent, withdraw.StatusCode);
            }
            using (var readDenied = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, readDenied.StatusCode); }
            using (var writeDenied = await user.PostAsJsonAsync(Path("/api/costing/cost"), input)) { Assert.Equal(HttpStatusCode.Forbidden, writeDenied.StatusCode); }
            using (var directDenied = await costing.Client.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, directDenied.StatusCode); }
            // Persisted withdrawal is still effective after the authority process restarts, using the original JWT.
            await platform.CrashAsync();
            await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.ServiceUnavailable);
            await using var restarted = await PlatformHostProcess.StartAsync(identity.ConnectionString, settings: settings, listenAddress: platform.Client.BaseAddress);
            using (var afterRestart = await costing.Client.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, afterRestart.StatusCode); }
            await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
            foreach (var id in new[] { menuId, writeMenuId })
            { using var restore = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{id}"), null); Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode); }
            using (var restored = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.OK, restored.StatusCode); }
            using (var userState = await root.GetAsync(Path($"/api/identity/users/{userId}")))
            {
                var version = (await userState.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64();
                using var revoke = await root.PostAsJsonAsync(Path($"/api/identity/users/{userId}/roles/{roleId}/revoke"), new { expectedVersion = version });
                Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            }
            using (var revoked = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode); }
            await restarted.CrashAsync();
            await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.ServiceUnavailable);
            await using var afterRoleWithdrawal = await PlatformHostProcess.StartAsync(identity.ConnectionString, settings: settings, listenAddress: restarted.Client.BaseAddress);
            using (var persistedRoleWithdrawal = await costing.Client.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.Forbidden, persistedRoleWithdrawal.StatusCode); }
            using (var acceptedHistory = await root.GetAsync(Path($"/api/costing/tasks/{taskId}"))) { Assert.Equal(HttpStatusCode.OK, acceptedHistory.StatusCode); }
            await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
            using (var reassign = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, reassign.StatusCode); }
            using (var unchangedToken = await user.GetAsync(Path("/api/costing/tasks"))) { Assert.Equal(HttpStatusCode.OK, unchangedToken.StatusCode); }
        }
        finally { File.Delete(routes); }
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(Path(path), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static async Task WriteRoutesAsync(string path, Uri platform, Uri costing, Uri pricing)
    {
        var table = JsonNode.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            if (cluster!["clusterId"]!.GetValue<string>() == "platform-host")
            {
                // One failed probe makes both crash/recovery transitions observable; retain the shipped probe interval and timeout.
                cluster["healthCheck"] = JsonNode.Parse("""{"path":"/health/ready","interval":"00:00:05","timeout":"00:00:02","failureThreshold":1}""");
            }
            var address = cluster!["clusterId"]!.GetValue<string>() switch { "costing-host" => costing, "pricing-host" => pricing, _ => platform };
            foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address.AbsoluteUri; }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
    }

    private static Uri Path(string path) => new(path, UriKind.Relative);
}
