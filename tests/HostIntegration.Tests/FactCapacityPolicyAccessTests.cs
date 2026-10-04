using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyAccessTests
{
    [Theory]
    [InlineData("routes.json", "platform")]
    [InlineData("routes.pricing.json", "platform")]
    [InlineData("routes.business.json", "platform")]
    [InlineData("routes.json", "identity")]
    [InlineData("routes.pricing.json", "identity")]
    [InlineData("routes.business.json", "identity")]
    [InlineData("routes.json", "files")]
    [InlineData("routes.pricing.json", "files")]
    [InlineData("routes.business.json", "files")]
    [InlineData("routes.json", "scheduling")]
    [InlineData("routes.pricing.json", "scheduling")]
    [InlineData("routes.business.json", "scheduling")]
    public async Task GatewayPolicyWrite_RequiresSeparateCurrentPermissionAndTrustedActor_WithExactInt64(string configurationFile, string context)
    {
        await using var platform = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        platform.UseKestrel(0);
        using var root = platform.CreateClient();
        using var directUser = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(root.BaseAddress!.AbsoluteUri)
        { SigningKey = "integration-test-signing-key-long-enough-for-hs256" };
        var routes = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        gateway.UseRoutes(routes.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var user = gateway.CreateClient();
        var path = new Uri($"/api/{context}/audit-capacity", UriKind.Relative);
        var request = new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = "1",
            maxRecords = "9007199254740993",
            maxPayloadBytes = context == "identity" ? "268435456" : "16384",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
        };
        using var anonymous = await user.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        var account = await CreateAsync(root, "/api/identity/users", new { userName = "policy-reader", password = "policy-reader-password" });
        var userId = account.GetProperty("userId").ReadHttpInt64();
        await PlatformSettingsAccessTests.LoginAsync(directUser, "policy-reader", "policy-reader-password");
        user.DefaultRequestHeaders.Authorization = directUser.DefaultRequestHeaders.Authorization;
        using var denied = await user.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var readMenu = await CreateAsync(root, "/api/identity/menus", new { title = "Policy read", sortOrder = 1 });
        var readMenuId = readMenu.GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = path.OriginalString, method = "GET", menuId = readMenuId });
        var writeMenu = await CreateAsync(root, "/api/identity/menus", new { title = "Policy write", sortOrder = 2 });
        var writeMenuId = writeMenu.GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = path.OriginalString, method = "PUT", menuId = writeMenuId });
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "policy-operator", name = "Policy operator" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        using var grantRead = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{readMenuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grantRead.StatusCode);
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using var allowedRead = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowedRead.StatusCode);
        using var readCannotWrite = await user.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Forbidden, readCannotWrite.StatusCode);
        using var grantWrite = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{writeMenuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grantWrite.StatusCode);
        using var accepted = await user.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        Assert.Equal("9007199254740993", receipt.GetProperty("current").GetProperty("maxRecords").GetString());
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
        using var current = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var snapshot = await current.Content.ReadApiDataAsync();
        Assert.Equal("9007199254740993", snapshot.GetProperty("maxRecords").GetString());
        await using var scope = platform.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var eventName = $"{context}.fact-capacity-policy-changed.v1";
        var fact = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow), entry => entry.EventName == eventName);
        using var payload = JsonDocument.Parse(fact.Payload);
        Assert.Equal(userId.ToString(CultureInfo.InvariantCulture), payload.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(9007199254740993, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        Assert.Equal(request.requestId, payload.RootElement.GetProperty("requestId").GetGuid());

        // Revocation has no HTTP command yet; arrange it through existing owned repository and commit ports.
        var stored = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(roleId)]));
        Assert.True(stored.Revoke(new MenuId(writeMenuId), DateTimeOffset.UtcNow).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IPermissionCache>().Invalidate();
        var next = new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = "2",
            maxRecords = "9007199254740994",
            maxPayloadBytes = request.maxPayloadBytes,
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
        };
        using var revoked = await user.PutAsJsonAsync(path, next);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var stillRead = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, stillRead.StatusCode);
        var afterRevocation = await stillRead.Content.ReadApiDataAsync();
        Assert.Equal(snapshot.GetProperty("policyRevision").GetRawText(), afterRevocation.GetProperty("policyRevision").GetRawText());
        Assert.Equal(snapshot.GetProperty("maxRecords").GetRawText(), afterRevocation.GetProperty("maxRecords").GetRawText());
        Assert.Equal(snapshot.GetProperty("controlCapacity").GetRawText(), afterRevocation.GetProperty("controlCapacity").GetRawText());
        // Revoking a role is an Identity business change; it must not be mistaken for a policy change.
        Assert.Equal(snapshot.GetProperty("retainedRecords").ReadHttpInt64() + (context == "identity" ? 1 : 0),
            afterRevocation.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(fact, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow), entry => entry.EventName == eventName));
        using var logout = await user.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var closedSession = await user.PutAsJsonAsync(path, next);
        Assert.Equal(HttpStatusCode.Unauthorized, closedSession.StatusCode);
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
