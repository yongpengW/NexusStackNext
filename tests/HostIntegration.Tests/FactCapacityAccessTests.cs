using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityAccessTests
{
    [Theory]
    [InlineData("routes.json")]
    [InlineData("routes.pricing.json")]
    [InlineData("routes.business.json")]
    public async Task ShippedGatewayRoutes_ForwardAuthorizedCapacityReads_WithPublishedResponseContracts(string configurationFile)
    {
        await using var platform = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        platform.UseKestrel(0);
        using var direct = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(direct.BaseAddress!.AbsoluteUri)
        { SigningKey = "integration-test-signing-key-long-enough-for-hs256" };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var client = gateway.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(direct, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var document = await direct.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        foreach (var owner in new[] { "platform", "identity", "files", "scheduling" })
        {
            var path = new Uri($"/api/{owner}/audit-capacity", UriKind.Relative);
            client.DefaultRequestHeaders.Authorization = null;
            using var anonymous = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Authorization = direct.DefaultRequestHeaders.Authorization;
            using var allowed = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal(owner, (await allowed.Content.ReadApiDataAsync()).GetProperty("context").GetString());
            AssertCapacitySchema(document, path.OriginalString);
        }
    }

    internal static void AssertCapacitySchema(JsonElement document, string path)
    {
        var responses = document.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses");
        foreach (var code in new[] { "401", "403", "503" }) { Assert.True(responses.TryGetProperty(code, out _)); }
        var envelope = HttpInt64OpenApiTests.Resolve(document, responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema"));
        var data = HttpInt64OpenApiTests.Resolve(document, envelope.GetProperty("properties").GetProperty("data")).GetProperty("properties");
        foreach (var name in new[] { "maxRecords", "maxPayloadBytes", "retainedRecords", "retainedPayloadBytes", "remainingRecords", "remainingPayloadBytes" })
        { HttpInt64OpenApiTests.AssertOutput(data.GetProperty(name), nullable: false); }
        var integerType = data.GetProperty("maxRecordPayloadBytes").GetProperty("type");
        Assert.Contains("integer", integerType.ValueKind == JsonValueKind.Array
            ? integerType.EnumerateArray().Select(item => item.GetString()) : [integerType.GetString()]);
        Assert.Equal("boolean", data.GetProperty("isPersistent").GetProperty("type").GetString());
        Assert.Equal("boolean", data.GetProperty("overLimit").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CapacityRead_IsObserved_WithoutNewCommittedFactsOrPermissionInvalidation()
    {
        var cache = new RecordingPermissionCache();
        await using var baseApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IPermissionCache>(cache)));
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        _ = await CreateAsync(client, "/api/identity/api-resources", new { path = "/diagnostics-proof", method = "GET" });
        var invalidations = cache.Invalidations;
        Assert.True(invalidations > 0);
        await using var scope = app.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        foreach (var owner in new[] { "platform", "identity", "files", "scheduling" })
        {
            var source = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(owner);
            var before = await source.ReadPendingAsync(100, DateTimeOffset.UtcNow);
            var path = $"/api/{owner}/audit-capacity";
            var correlation = "capacity-read-" + Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Correlation-Id", correlation);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var observed = await OperationEndpointInventoryTests.WaitAsync(journal, correlation);
            Assert.Equal(path, observed.RouteTemplate);
            Assert.Equal(200, observed.StatusCode);
            Assert.Equal(before, await source.ReadPendingAsync(100, DateTimeOffset.UtcNow));
            Assert.Equal(invalidations, cache.Invalidations);
        }
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task CapacityReader_RequiresItsOwnGrant_AndRevocationRejectsExistingToken(string context)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        var path = new Uri($"/api/{context}/audit-capacity", UriKind.Relative);
        using var anonymous = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var created = await CreateAsync(root, "/api/identity/users", new { userName = "capacity-reader", password = "capacity-reader-password" });
        var userId = created.GetProperty("userId").ReadHttpInt64();
        await PlatformSettingsAccessTests.LoginAsync(user, "capacity-reader", "capacity-reader-password");
        using var denied = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var menu = await CreateAsync(root, "/api/identity/menus", new { title = "Capacity read", sortOrder = 1 });
        var menuId = menu.GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = path.OriginalString, method = "GET", menuId });
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "capacity-reader", name = "Capacity reader" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        using var grant = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using var allowed = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(context, (await allowed.Content.ReadApiDataAsync()).GetProperty("context").GetString());
        var other = context == "platform" ? "identity" : "platform";
        using var unrelated = await user.GetAsync(new Uri($"/api/{other}/audit-capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, unrelated.StatusCode);

        // 撤权用例尚未提供；按现有公开仓储、UoW 与提交后失效端口安排撤权，验收当前令牌的读取权限。
        await using var scope = app.Services.CreateAsyncScope();
        var stored = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(roleId)]));
        Assert.True(stored.Revoke(new MenuId(menuId), DateTimeOffset.UtcNow).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IPermissionCache>().Invalidate();
        using var revoked = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var stillRoot = await root.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, stillRoot.StatusCode);
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
