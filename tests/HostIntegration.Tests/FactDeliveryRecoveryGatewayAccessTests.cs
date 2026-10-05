using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactDeliveryRecoveryGatewayAccessTests
{
    [Theory]
    [InlineData("platform", "routes.pricing.json")]
    [InlineData("platform", "routes.business.json")]
    [InlineData("identity", "routes.json")]
    [InlineData("identity", "routes.pricing.json")]
    [InlineData("identity", "routes.business.json")]
    [InlineData("files", "routes.json")]
    [InlineData("files", "routes.pricing.json")]
    [InlineData("files", "routes.business.json")]
    [InlineData("scheduling", "routes.json")]
    [InlineData("scheduling", "routes.pricing.json")]
    [InlineData("scheduling", "routes.business.json")]
    public Task PlatformHostMemory_AllRouteVariantsRequireCurrentRecoveryPermissionsAndSessions(string source, string configurationFile)
        => VerifyMemoryAsync(source, configurationFile);

    [PostgresFact]
    public Task PlatformPostgres_DefaultRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("platform", "routes.json");

    [PostgresFact]
    public Task PlatformPostgres_PricingRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("platform", "routes.pricing.json");

    [PostgresFact]
    public Task PlatformPostgres_BusinessRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("platform", "routes.business.json");

    [PostgresFact]
    public Task IdentityPostgres_DefaultRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("identity", "routes.json");

    [PostgresFact]
    public Task IdentityPostgres_PricingRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("identity", "routes.pricing.json");

    [PostgresFact]
    public Task IdentityPostgres_BusinessRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("identity", "routes.business.json");

    [PostgresFact]
    public Task FilesPostgres_DefaultRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("files", "routes.json");

    [PostgresFact]
    public Task FilesPostgres_PricingRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("files", "routes.pricing.json");

    [PostgresFact]
    public Task FilesPostgres_BusinessRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("files", "routes.business.json");

    [PostgresFact]
    public Task SchedulingPostgres_DefaultRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("scheduling", "routes.json");

    [PostgresFact]
    public Task SchedulingPostgres_PricingRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("scheduling", "routes.pricing.json");

    [PostgresFact]
    public Task SchedulingPostgres_BusinessRoutes_RequireCurrentRecoveryPermissionsAndSessions()
        => VerifyPostgresAsync("scheduling", "routes.business.json");

    [Fact]
    public Task PlatformMemory_DefaultRoutes_RequireIndependentCurrentPermissionsAndValidRootSession()
        => VerifyMemoryAsync("platform", "routes.json");

    private static async Task VerifyMemoryAsync(string source, string configurationFile)
    {
        await using var platform = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        platform.UseKestrel(0);
        using var root = platform.CreateClient();
        await VerifyAsync(root, platform.Services, source, configurationFile, PlatformAppWithRootAccount.RootUserName);
    }

    private static async Task VerifyPostgresAsync(string source, string configurationFile)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var platform = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var root = platform.CreateClient();
        await VerifyAsync(root, platform.Services, source, configurationFile, "journey-root");
    }

    private static async Task VerifyAsync(HttpClient root, IServiceProvider services, string source, string configurationFile,
        string rootUserName)
    {
        await using var gateway = new GatewayHttpApp(root.BaseAddress!.AbsoluteUri)
        { SigningKey = "integration-test-signing-key-long-enough-for-hs256" };
        var routes = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, configurationFile))).Value;
        gateway.UseRoutes(routes.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var user = gateway.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, rootUserName, PlatformAppWithRootAccount.RootPassword);
        var account = await CreateAsync(root, "/api/identity/users", new { userName = "recovery-reader", password = "recovery-reader-password" });
        var userId = account.GetProperty("userId").ReadHttpInt64();
        var readMenu = await CreateAsync(root, "/api/identity/menus", new { title = "Recovery read", sortOrder = 1 });
        var readMenuId = readMenu.GetProperty("menuId").ReadHttpInt64();
        var writeMenu = await CreateAsync(root, "/api/identity/menus", new { title = "Recovery write", sortOrder = 2 });
        var writeMenuId = writeMenu.GetProperty("menuId").ReadHttpInt64();
        var role = await CreateAsync(root, "/api/identity/roles", new { code = "recovery-operator", name = "Recovery operator" });
        var roleId = role.GetProperty("roleId").ReadHttpInt64();
        var path = $"/api/{source}/audit-deliveries";
        foreach (var template in new[] { path, path + "/{messageId}", path + "/recovery-capacity", path + "/recoveries/{requestId}" })
        {
            _ = await CreateAsync(root, "/api/identity/api-resources", new { path = template, method = "GET", menuId = readMenuId });
        }
        _ = await CreateAsync(root, "/api/identity/api-resources",
            new { path = path + "/{messageId}/retry", method = "POST", menuId = writeMenuId });
        using var assign = await root.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        await using var scope = services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source);
        var current = (await policies.ReadPolicyAsync()).Value;
        var policy = await policies.AdjustAsync(new(Guid.NewGuid(), current.PolicyRevision, current.MaxRecords + 1,
            current.MaxPayloadBytes, current.MaxRecordPayloadBytes, "operator-adjustment"), "fixture-operator", DateTimeOffset.UtcNow, null);
        Assert.True(policy.IsSuccess);
        Assert.NotNull(policy.Value.EventId);
        var messageId = policy.Value.EventId.Value;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source);
        var original = Assert.Single(await outbox.ReadPendingAsync(1000, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.True((await publisher.PublishPendingAsync()).DeadLettered > 0);
        var requestId = Guid.NewGuid();
        var retryPath = new Uri($"{path}/{messageId}/retry", UriKind.Relative);
        var readPaths = new[] { path + "?state=DeadLettered", $"{path}/{messageId}", path + "/recovery-capacity",
            $"{path}/recoveries/{requestId}" };
        var request = new
        {
            requestId,
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "dependency-restored",
            actorId = "forged-actor",
            source = "forged-source",
            recoveredAt = DateTimeOffset.MaxValue,
        };
        await AssertReadsAsync(user, readPaths, HttpStatusCode.Unauthorized);
        using var anonymous = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(user, "recovery-reader", "recovery-reader-password");
        await AssertReadsAsync(user, readPaths, HttpStatusCode.Forbidden);
        using var denied = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var grantRead = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{readMenuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grantRead.StatusCode);
        await AssertReadsAsync(user, readPaths[..3], HttpStatusCode.OK);
        using var absentReceipt = await user.GetAsync(new Uri(readPaths[3], UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, absentReceipt.StatusCode);
        using var readOnly = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        using var stopped = await user.GetAsync(new Uri(readPaths[1], UriKind.Relative));
        var state = await stopped.Content.ReadApiDataAsync();
        Assert.Equal("DeadLettered", state.GetProperty("state").GetString());
        Assert.Equal("0", state.GetProperty("retryRevision").GetString());
        Assert.False(state.TryGetProperty("payload", out _));
        Assert.False(state.TryGetProperty("lastFailure", out _));

        using var grantWrite = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{writeMenuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, grantWrite.StatusCode);
        await RevokeAsync(services, roleId, readMenuId);
        await AssertReadsAsync(user, readPaths, HttpStatusCode.Forbidden);
        var beforePools = (await policies.ReadPolicyAsync()).Value;
        using var accepted = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        Assert.Equal(source, receipt.GetProperty("source").GetString());
        Assert.Equal(userId.ToString(CultureInfo.InvariantCulture), receipt.GetProperty("actorId").GetString());
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(messageId, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal("0", receipt.GetProperty("expectedRetryRevision").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        Assert.NotEqual(DateTimeOffset.MaxValue, receipt.GetProperty("recoveredAt").GetDateTimeOffset());
        Assert.NotEqual(JsonValueKind.Null, receipt.GetProperty("execution").ValueKind);
        Assert.Equal(beforePools, (await policies.ReadPolicyAsync()).Value);
        var recovered = Assert.Single(await outbox.ReadPendingAsync(1000, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(original.Payload, recovered.Payload);
        Assert.Equal(original.OccurredAt, recovered.OccurredAt);
        Assert.Equal(original.EventName, recovered.EventName);
        using var replay = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var restoreRead = await root.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{readMenuId}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, restoreRead.StatusCode);
        await AssertReadsAsync(user, readPaths, HttpStatusCode.OK);
        using var found = await user.GetAsync(new Uri(readPaths[3], UriKind.Relative));
        Assert.Equal(receipt.GetRawText(), (await found.Content.ReadApiDataAsync()).GetRawText());
        using var capacity = await user.GetAsync(new Uri(readPaths[2], UriKind.Relative));
        Assert.Equal("1", (await capacity.Content.ReadApiDataAsync()).GetProperty("capacity").GetProperty("retainedRecords").GetString());

        await RevokeAsync(services, roleId, writeMenuId);
        using var revokedReplay = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        await AssertReadsAsync(user, readPaths, HttpStatusCode.OK);
        using var logout = await user.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await AssertReadsAsync(user, readPaths, HttpStatusCode.Unauthorized);
        using var revokedSessionReplay = await user.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedSessionReplay.StatusCode);

        using var edgeRoot = gateway.CreateClient();
        edgeRoot.DefaultRequestHeaders.Authorization = root.DefaultRequestHeaders.Authorization;
        await AssertReadsAsync(edgeRoot, readPaths, HttpStatusCode.OK);
        using var rootLogout = await edgeRoot.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NoContent, rootLogout.StatusCode);
        await AssertReadsAsync(edgeRoot, readPaths, HttpStatusCode.Unauthorized);
        using var invalidRootReplay = await edgeRoot.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidRootReplay.StatusCode);
    }

    private static async Task RevokeAsync(IServiceProvider services, long roleId, long menuId)
    {
        // Arrange committed revocation through Identity's owned public ports; no HTTP revoke command exists yet.
        await using var scope = services.CreateAsyncScope();
        var role = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindManyAsync([new RoleId(roleId)]));
        Assert.True(role.Revoke(new MenuId(menuId), DateTimeOffset.UtcNow).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IPermissionCache>().Invalidate();
    }

    private static async Task AssertReadsAsync(HttpClient client, IEnumerable<string> paths, HttpStatusCode expected)
    {
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(expected, response.StatusCode);
        }
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
