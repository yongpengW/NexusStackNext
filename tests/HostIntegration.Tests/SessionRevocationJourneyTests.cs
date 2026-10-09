using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SessionRevocationJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task SameRootToken_AfterLogout_IsRejectedByTheEdgeAndDirectBusinessHosts()
        => await RootRevocationAcrossConsumersAsync(false);

    [PostgresFact]
    public async Task Root_password_rotation_revokes_all_consumers_and_survives_restart_without_seed_overwrite()
        => await RootRevocationAcrossConsumersAsync(true);

    private async Task RootRevocationAcrossConsumersAsync(bool rotatePassword)
    {
        await using var identity = await databases.CreateAsync();
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        var settings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(identity.ConnectionString, "session-root-test-password", settings: settings);
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: settings);
        await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString, settings: settings);
        var routes = Path.Combine(Path.GetTempPath(), "nsn-session-routes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.business.json")))!;
            foreach (var cluster in table["clusters"]!.AsArray())
            {
                var address = cluster!["clusterId"]!.GetValue<string>() switch
                {
                    "costing-host" => costing.Client.BaseAddress,
                    "pricing-host" => pricing.Client.BaseAddress,
                    _ => platform.Client.BaseAddress,
                };
                foreach (var destination in cluster["destinations"]!.AsArray()) { destination!["address"] = address!.AbsoluteUri; }
            }
            await File.WriteAllTextAsync(routes, table.ToJsonString());
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routes, settings);
            var initialTokens = await UserLifecycleHttpTests.LoginAsync(gateway.Client, "journey-root", "session-root-test-password");
            costing.Client.DefaultRequestHeaders.Authorization = gateway.Client.DefaultRequestHeaders.Authorization;
            pricing.Client.DefaultRequestHeaders.Authorization = gateway.Client.DefaultRequestHeaders.Authorization;
            var checks = new[]
            {
                (gateway.Client, "/api/platform/settings/mail.sender"),
                (gateway.Client, "/api/costing/audit-capacity"),
                (gateway.Client, "/api/pricing/audit-capacity"),
                (gateway.Client, "/gateway/routes/pricing"),
                (costing.Client, "/api/costing/audit-capacity"),
                (pricing.Client, "/api/pricing/audit-capacity"),
            };
            foreach (var (client, path) in checks)
            {
                using var before = await client.GetAsync(new Uri(path, UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            }
            await RevokeRootAsync(gateway.Client, rotatePassword);
            using var oldRefresh = await gateway.Client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative),
                new { refreshToken = initialTokens.GetProperty("refreshToken").GetString() });
            Assert.Equal(HttpStatusCode.BadRequest, oldRefresh.StatusCode);
            foreach (var (client, path) in checks)
            {
                using var after = await client.GetAsync(new Uri(path, UriKind.Relative));
                Assert.True(after.StatusCode == HttpStatusCode.Unauthorized, $"Revoked session reached {path}: {(int)after.StatusCode}.");
            }
            await platform.CrashAsync();
            using (var offline = await costing.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m }))
            { Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode); }
            using (var offline = await pricing.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", cost = 80m, feeRate = 0.2m }))
            { Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode); }
            using (var offline = await gateway.Client.DeleteAsync(new Uri("/gateway/routes/pricing", UriKind.Relative)))
            { Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode); }
            await using var restarted = await PlatformHostProcess.StartAsync(identity.ConnectionString, "session-root-test-password",
                settings: settings, listenAddress: platform.Client.BaseAddress);
            await BusinessProcess.WaitForIdentityForwardingAsync(gateway.Client, HttpStatusCode.OK);
            foreach (var (client, path) in checks)
            {
                using var stillRevoked = await client.GetAsync(new Uri(path, UriKind.Relative));
                Assert.Equal(HttpStatusCode.Unauthorized, stillRevoked.StatusCode);
            }
            using var login = await gateway.Client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
                new { userName = "journey-root", password = rotatePassword ? "session-root-rotated-password" : "session-root-test-password" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var tokens = await login.Content.ReadApiDataAsync();
            var accessToken = tokens.GetProperty("accessToken").GetString()!;
            foreach (var (client, _) in checks) { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); }
            foreach (var (client, path) in checks)
            {
                using var fresh = await client.GetAsync(new Uri(path, UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            }
            foreach (var kind in new[] { "missing-subject", "missing-version", "wrong-version", "expired", "issuer", "audience", "signature" })
            {
                var invalid = InvalidToken(accessToken, kind);
                foreach (var (client, path) in checks)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", invalid);
                    using var denied = await client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                }
            }
            var refresh = new { refreshToken = tokens.GetProperty("refreshToken").GetString() };
            using var replacement = await gateway.Client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), refresh);
            Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
            var replacementToken = (await replacement.Content.ReadApiDataAsync()).GetProperty("accessToken").GetString();
            foreach (var (client, _) in checks) { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacementToken); }
            using var replay = await gateway.Client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), refresh);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            foreach (var (client, path) in checks)
            {
                using var denied = await client.GetAsync(new Uri(path, UriKind.Relative));
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        finally { File.Delete(routes); }
    }

    [PostgresFact]
    public async Task FaultedAuthorityResponses_DoNotLeaveBusinessInputsOrTasks_AndRecoveryCanAcceptThem()
    {
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        var fault = "valid";
        await using var authority = await SessionAuthorityStub.StartAsync(BusinessProcess.SigningKey, async http =>
        {
            if (fault == "timeout") { await Task.Delay(TimeSpan.FromSeconds(10), http.RequestAborted); }
            if (fault == "malformed") { return Results.Text("invalid-json", "application/json"); }
            if (fault == "unavailable") { return Results.StatusCode(503); }
            if (fault == "invalid-session") { return Results.Unauthorized(); }
            if (fault == "redirect") { return Results.Redirect("/api/identity/session/v1"); }
            if (fault == "oversize") { return Results.Text(new string(' ', 4097), "application/json"); }
            if (fault == "missing-allow") { return Results.Json(new { success = true, code = 200, data = new { contractVersion = 1, subject = "test-operator", sessionVersion = "0", permissionKey = http.Request.Query["permissionKey"].ToString() } }); }
            return Results.Json(new
            {
                success = true,
                code = 200,
                data = new
                {
                    contractVersion = fault == "wrong-contract" ? 2 : 1,
                    subject = fault == "wrong-subject" ? "42" : "test-operator",
                    sessionVersion = fault == "wrong-session" ? "1" : "0",
                    permissionKey = fault == "wrong-operation" ? "/api/costing/tasks:GET" : http.Request.Query["permissionKey"].ToString(),
                    isAllowed = fault != "denied",
                },
            });
        });
        var settings = new Dictionary<string, string>
        { ["IdentitySession__BaseAddress"] = authority.Address, ["IdentitySession__Timeout"] = "00:00:00.500" };
        await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: settings);
        await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString, settings: settings);
        costing.Authenticate();
        pricing.Authenticate();
        foreach (var failure in new[] { "unavailable", "malformed", "timeout", "wrong-contract", "wrong-subject", "wrong-session", "wrong-operation", "missing-allow", "oversize", "redirect", "denied", "invalid-session" })
        {
            var item = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            foreach (var (host, context) in new[] { (costing, "costing"), (pricing, "pricing") })
            {
                fault = failure;
                object input = context == "costing" ? new { requestId, itemId = item, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m }
                    : new { requestId, itemId = item, expectedVersion = "0", cost = 100m, feeRate = 0.2m };
                using var denied = await host.Client.PostAsJsonAsync(new Uri($"/api/{context}/cost", UriKind.Relative), input);
                var expected = failure switch { "denied" => HttpStatusCode.Forbidden, "invalid-session" => HttpStatusCode.Unauthorized, _ => HttpStatusCode.ServiceUnavailable };
                Assert.Equal(expected, denied.StatusCode);
                fault = "valid";
                using var absent = await host.Client.GetAsync(new Uri($"/api/{context}/items/{item}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
                var tasks = await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/{context}/tasks?itemId={item}", UriKind.Relative));
                Assert.Empty(tasks.GetProperty("data").EnumerateArray());
                using var accepted = await host.Client.PostAsJsonAsync(new Uri($"/api/{context}/cost", UriKind.Relative), input);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
        }
    }

    [AuditBrokerFact]
    public async Task AlreadyAuthorizedCostCommit_MayFinishAfterLogout_AndItsTaskCompletesThroughPricingWithoutBearer()
        => await AcceptedTaskAfterRevocationAsync(false);

    [AuditBrokerFact]
    public async Task Accepted_cost_task_survives_root_password_rotation_and_worker_restart_through_pricing()
        => await AcceptedTaskAfterRevocationAsync(true);

    [AuditBrokerFact]
    public async Task Already_authorized_ordinary_cost_commit_survives_role_withdrawal_and_worker_restart_through_pricing()
        => await AcceptedTaskAfterRevocationAsync(false, revokeRole: true);

    private async Task AcceptedTaskAfterRevocationAsync(bool rotatePassword, bool revokeRole = false)
    {
        await using var identity = await databases.CreateAsync();
        await using var costs = await databases.CreateAsync("costing");
        await using var prices = await databases.CreateAsync("pricing");
        var key = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = key + "-session", ClientName = key };
        var subscription = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = key + "-pricing" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        var platformSettings = new Dictionary<string, string> { ["Jwt__SigningKey"] = BusinessProcess.SigningKey };
        await using var platform = await PlatformHostProcess.StartAsync(identity.ConnectionString, "session-root-test-password", settings: platformSettings);
        var settings = AuditBusinessJourneyTests.Settings(broker, key + "-auditing");
        settings["IdentitySession__BaseAddress"] = platform.Client.BaseAddress!.AbsoluteUri;
        settings["Costing__Scheduling__Enabled"] = "false";
        settings["Costing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__Enabled"] = "true";
        settings["Pricing__Messaging__ConsumerName"] = subscription.ConsumerName;
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", prices.ConnectionString, worker: true, settings: settings);
            await using var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(platform.Client, "journey-root", "session-root-test-password");
            var bearer = platform.Client.DefaultRequestHeaders.Authorization!;
            string? userId = null;
            string? roleId = null;
            if (revokeRole)
            {
                using var registered = await platform.Client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new { userName = "cost-operator", password = "cost-operator-password" });
                Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
                userId = (await registered.Content.ReadApiDataAsync()).GetProperty("userId").GetString();
                using var menu = await platform.Client.PostAsJsonAsync(new Uri("/api/identity/menus", UriKind.Relative), new { title = "Cost operator", sortOrder = 1 });
                Assert.Equal(HttpStatusCode.Created, menu.StatusCode);
                var menuId = (await menu.Content.ReadApiDataAsync()).GetProperty("menuId").GetString();
                foreach (var (path, method) in new[] { ("/api/costing/cost", "POST"), ("/api/costing/tasks/{taskId}", "GET"), ("/api/costing/tasks/{taskId}/retry", "POST"), ("/api/costing/tasks/{taskId}/cancel", "POST") })
                {
                    using var resource = await platform.Client.PostAsJsonAsync(new Uri("/api/identity/api-resources", UriKind.Relative), new { path, method, menuId });
                    Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
                }
                using var role = await platform.Client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative), new { code = "cost-operator", name = "Cost operator" });
                Assert.Equal(HttpStatusCode.Created, role.StatusCode);
                roleId = (await role.Content.ReadApiDataAsync()).GetProperty("roleId").GetString();
                using var granted = await platform.Client.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
                Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
                using var assigned = await platform.Client.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
                Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
                using var ordinary = new HttpClient { BaseAddress = platform.Client.BaseAddress };
                await UserLifecycleHttpTests.LoginAsync(ordinary, "cost-operator", "cost-operator-password");
                bearer = ordinary.DefaultRequestHeaders.Authorization!;
            }
            costing.Client.DefaultRequestHeaders.Authorization = bearer;
            var item = Guid.NewGuid();
            var task = Guid.NewGuid();
            await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(costs.ConnectionString) { Pooling = false }.ConnectionString);
            await barrier.OpenAsync();
            await using var locked = await barrier.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("LOCK TABLE costing.sheets IN ACCESS EXCLUSIVE MODE", barrier, locked)) { await command.ExecuteNonQueryAsync(); }
            var commit = costing.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                new { requestId = task, itemId = item, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m });
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await using var probe = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)))", barrier, locked);
                    probe.Parameters.AddWithValue("barrier", barrier.ProcessID);
                    if ((bool)(await probe.ExecuteScalarAsync(wait.Token))!) { break; }
                    await Task.Delay(20, wait.Token);
                }
                if (revokeRole)
                {
                    using var current = await platform.Client.GetAsync(new Uri($"/api/identity/users/{userId}", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                    var version = (await current.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64();
                    using var withdrawn = await platform.Client.PostAsJsonAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}/revoke", UriKind.Relative), new { expectedVersion = version });
                    Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
                }
                else { await RevokeRootAsync(platform.Client, rotatePassword); }
            }
            finally { await locked.RollbackAsync(); }
            using var accepted = await commit;
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            foreach (var path in new[] { $"/api/costing/tasks/{task}", $"/api/costing/tasks/{task}/retry", $"/api/costing/tasks/{task}/cancel", "/api/costing/cost" })
            {
                using var denied = path.EndsWith(task.ToString(), StringComparison.Ordinal)
                    ? await costing.Client.GetAsync(new Uri(path, UriKind.Relative))
                    : await costing.Client.PostAsJsonAsync(new Uri(path, UriKind.Relative), new { expectedEpoch = "0" });
                Assert.Equal(revokeRole ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            await costing.CrashAsync();
            await using var worker = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costs.ConnectionString, worker: true, settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(platform.Client, "journey-root", rotatePassword ? "session-root-rotated-password" : "session-root-test-password");
            worker.Client.DefaultRequestHeaders.Authorization = platform.Client.DefaultRequestHeaders.Authorization;
            pricing.Client.DefaultRequestHeaders.Authorization = platform.Client.DefaultRequestHeaders.Authorization;
            using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                using var response = await pricing.Client.GetAsync(new Uri($"/api/pricing/items/{item}", UriKind.Relative), completed.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var quote = await response.Content.ReadApiDataAsync();
                    if (quote.GetProperty("breakEvenPrice").ValueKind == JsonValueKind.Number)
                    { Assert.Equal(100m, quote.GetProperty("cost").GetDecimal()); break; }
                }
                await Task.Delay(100, completed.Token);
            }
            using var final = await worker.Client.GetAsync(new Uri($"/api/costing/tasks/{task}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, final.StatusCode);
            var publicState = await final.Content.ReadAsStringAsync();
            Assert.False(publicState.Contains(bearer.Parameter!, StringComparison.Ordinal), "Task state must not contain user Bearer.");
            var state = (await final.Content.ReadApiDataAsync()).GetProperty("state").GetString();
            Assert.Equal("Succeeded", state);
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private static async Task RevokeRootAsync(HttpClient client, bool rotatePassword)
    {
        if (!rotatePassword)
        {
            using var logout = await client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            return;
        }
        using var me = await client.GetAsync(new Uri("/api/identity/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var version = (await me.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64();
        using var rotated = await client.PostAsJsonAsync(new Uri("/api/identity/me/password", UriKind.Relative),
            new { expectedVersion = version, oldPassword = "session-root-test-password", newPassword = "session-root-rotated-password" });
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
    }

    private static string InvalidToken(string current, string kind)
    {
        var original = new JwtSecurityTokenHandler().ReadJwtToken(current);
        var claims = original.Claims.Where(claim => claim.Type is not ("iss" or "aud" or "exp" or "nbf" or "iat")
            && (kind != "missing-subject" || claim.Type is not ("sub" or ClaimTypes.NameIdentifier))
            && (kind is not ("missing-version" or "wrong-version") || claim.Type != NexusStackClaims.Session)).ToList();
        if (kind == "wrong-version") { claims.Add(new Claim(NexusStackClaims.Session, "999999")); }
        var token = new JwtSecurityToken(kind == "issuer" ? "untrusted" : original.Issuer,
            kind == "audience" ? "untrusted" : original.Audiences.Single(), claims, DateTime.UtcNow.AddMinutes(-10),
            kind == "expired" ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(kind == "signature" ? new string('x', 64) : BusinessProcess.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
