using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityCommittedAuditTests
{
    [AuditBrokerFact]
    public async Task PermissionAndTokenFacts_SurviveRestart_AndRetainTheRejectedReplayCommit()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-security", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = "identity.entity-committed.v1", ConsumerName = prefix + "-identity" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
            new EventSubscription { EventName = "auditing.operation-observed.v1", ConsumerName = prefix + "-operations" },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            long userId;
            long roleId;
            long resourceId;
            long menuId;
            string refreshToken;
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password"))
            {
                var client = source.Client;
                await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-root-password");
                client.DefaultRequestHeaders.Add("X-Correlation-ID", "security-facts-restarted");
                using var user = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                    new { userName = "private-security-user", password = "private-security-password" });
                Assert.Equal(HttpStatusCode.Created, user.StatusCode);
                userId = (await user.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
                using var menu = await client.PostAsJsonAsync(new Uri("/api/identity/menus", UriKind.Relative),
                    new { title = "Private security menu", sortOrder = 0, parentMenuId = (long?)null });
                Assert.Equal(HttpStatusCode.Created, menu.StatusCode);
                menuId = (await menu.Content.ReadApiDataAsync()).GetProperty("menuId").ReadHttpInt64();
                using var resource = await client.PostAsJsonAsync(new Uri("/api/identity/api-resources", UriKind.Relative),
                    new { path = "/private-security-resource", method = "GET", menuId });
                Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
                resourceId = (await resource.Content.ReadApiDataAsync()).GetProperty("apiResourceId").ReadHttpInt64();
                using var role = await client.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
                    new { code = "security-fact-role", name = "Private security role" });
                Assert.Equal(HttpStatusCode.Created, role.StatusCode);
                roleId = (await role.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var grant = await client.PostAsync(new Uri($"/api/identity/roles/{roleId}/menus/{menuId}", UriKind.Relative), null);
                    Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
                    using var assign = await client.PostAsync(new Uri($"/api/identity/users/{userId}/roles/{roleId}", UriKind.Relative), null);
                    Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
                }
                client.DefaultRequestHeaders.Authorization = null;
                using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
                    new { userName = "private-security-user", password = "private-security-password" });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                refreshToken = (await login.Content.ReadApiDataAsync()).GetProperty("refreshToken").GetString()!;
                using var refreshed = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), new { refreshToken });
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var replay = await client.PostAsJsonAsync(new Uri("/api/identity/refresh", UriKind.Relative), new { refreshToken });
                    Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
                }
                await source.CrashAsync();
            }
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password",
                settings: AuditBusinessJourneyTests.Settings(broker, prefix));
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "audit-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri("/api/auditing/entries?source=identity&correlationId=security-facts-restarted", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                facts = page.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("fact").Clone()).ToArray();
                Assert.True(facts.Length <= 12, "重复授权、重放或重启不应增加已提交事实。");
                if (facts.Length == 12) { break; }
                await Task.Delay(100, timeout.Token);
            }
            var users = facts.Where(fact => fact.GetProperty("subjectType").GetString() == "user")
                .OrderBy(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()).ToArray();
            Assert.Equal(new[] { "identity.user.created", "identity.user.role-assigned", "identity.user.login-succeeded", "identity.user.sessions-revoked" },
                users.Select(fact => fact.GetProperty("action").GetString()));
            Assert.All(users, fact => Assert.Equal(userId.ToString(CultureInfo.InvariantCulture), fact.GetProperty("subjectId").GetString()));
            Assert.Equal(roleId.ToString(CultureInfo.InvariantCulture), users[1].GetProperty("relatedSubject").GetProperty("id").GetString());
            var roles = facts.Where(fact => fact.GetProperty("subjectType").GetString() == "role").ToArray();
            Assert.Equal(2, roles.Length);
            Assert.All(roles, fact => Assert.Equal(roleId.ToString(CultureInfo.InvariantCulture), fact.GetProperty("subjectId").GetString()));
            var grantedMenu = Assert.Single(roles, fact => fact.GetProperty("action").GetString() == "identity.role.menu-granted");
            Assert.Equal(menuId.ToString(CultureInfo.InvariantCulture), grantedMenu.GetProperty("relatedSubject").GetProperty("id").GetString());
            var resourceFact = Assert.Single(facts, fact => fact.GetProperty("subjectType").GetString() == "api-resource");
            Assert.Equal(resourceId.ToString(CultureInfo.InvariantCulture), resourceFact.GetProperty("subjectId").GetString());
            Assert.Equal(menuId.ToString(CultureInfo.InvariantCulture), resourceFact.GetProperty("relatedSubject").GetProperty("id").GetString());
            var trees = facts.Where(fact => fact.GetProperty("subjectType").GetString() == "menu-tree").ToArray();
            Assert.Equal(2, trees.Length);
            Assert.All(trees, fact => Assert.Equal(2, fact.GetProperty("subjectVersion").ReadHttpInt64()));
            var nodeAdded = Assert.Single(trees, fact => fact.GetProperty("action").GetString() == "identity.menu-tree.node-added");
            Assert.Equal(menuId.ToString(CultureInfo.InvariantCulture), nodeAdded.GetProperty("relatedSubject").GetProperty("id").GetString());
            Assert.Equal(3, facts.Count(fact => fact.GetProperty("subjectType").GetString() == "refresh-token"));
            foreach (var tokenFact in facts.Where(fact => fact.GetProperty("subjectType").GetString() == "refresh-token"))
            {
                var owner = tokenFact.GetProperty("relatedSubject");
                Assert.Equal("identity", owner.GetProperty("context").GetString());
                Assert.Equal("user", owner.GetProperty("type").GetString());
                Assert.Equal(userId.ToString(CultureInfo.InvariantCulture), owner.GetProperty("id").GetString());
            }
            using var byOwner = await resumed.Client.GetAsync(new Uri($"/api/auditing/entries?relatedContext=identity&relatedSubjectType=user&relatedSubjectId={userId}", UriKind.Relative), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
            var ownedTokens = await byOwner.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.Equal(3, ownedTokens.GetProperty("total").ReadHttpInt64());
            Assert.All(ownedTokens.GetProperty("data").EnumerateArray(), entry => Assert.Equal("refresh-token", entry.GetProperty("fact").GetProperty("subjectType").GetString()));
            foreach (var fact in facts)
            {
                Assert.Equal("platform", fact.GetProperty("execution").GetProperty("source").GetString());
                Assert.DoesNotContain("private", fact.GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.False(fact.GetRawText().Contains(refreshToken, StringComparison.Ordinal));
            }
            var revoked = users[^1];
            Assert.Equal(JsonValueKind.Null, revoked.GetProperty("actorId").ValueKind);
            var operationId = revoked.GetProperty("execution").GetProperty("operationId").GetGuid();
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/operations?source=platform&operationId={operationId}", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var observations = (await response.Content.ReadApiDataAsync()).EnumerateArray().ToArray();
                if (observations.Length == 1 && observations[0].GetProperty("outcome").GetString() == "rejected") { break; }
                await Task.Delay(100, timeout.Token);
            }
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [PostgresFact]
    public async Task SourceFactWriteFailure_RollsBackRegistration_AndRetryCreatesOneUserAndFact()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION identity.reject_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected source fact failure'; END $$;
            CREATE TRIGGER reject_fact BEFORE INSERT ON identity.outbox
            FOR EACH ROW EXECUTE FUNCTION identity.reject_fact();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        var request = new { userName = "atomic-fact-user", password = "private-atomic-password" };
        using var failed = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = new EfOutboxStore<IdentityDbContext>(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
        await using (var recover = new NpgsqlCommand("DROP TRIGGER reject_fact ON identity.outbox", connection))
        { await recover.ExecuteNonQueryAsync(); }
        using var retried = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var id = (await retried.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64().ToString(CultureInfo.InvariantCulture);
        var fact = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
        using var payload = JsonDocument.Parse(fact.Payload);
        Assert.Equal(id, payload.RootElement.GetProperty("subjectId").GetString());
    }

    [AuditBrokerFact]
    public async Task RegisteredUserFact_SurvivesSourceRestart_AndReachesAuthorizedInvestigation()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-identity", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = "identity.entity-committed.v1", ConsumerName = prefix + "-identity" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
            new EventSubscription { EventName = "auditing.operation-observed.v1", ConsumerName = prefix + "-operations" },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            string id;
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password"))
            {
                source.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "identity-restarted-registration");
                using var created = await source.Client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                    new { userName = "private-restarted-user", password = "private-restarted-password" });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                id = (await created.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64().ToString(CultureInfo.InvariantCulture);
                await source.CrashAsync();
            }
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: settings);
            using var denied = await resumed.Client.GetAsync(new Uri("/api/auditing/entries?source=identity", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "audit-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/entries?source=identity&subjectType=user&subjectId={id}", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                if (page.GetProperty("total").ReadHttpInt64() == 1)
                {
                    var fact = Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact");
                    Assert.Equal("identity.user.created", fact.GetProperty("action").GetString());
                    Assert.Equal(JsonValueKind.Null, fact.GetProperty("actorId").ValueKind);
                    Assert.Equal("identity-restarted-registration", fact.GetProperty("correlationId").GetString());
                    Assert.Equal("platform", fact.GetProperty("execution").GetProperty("source").GetString());
                    Assert.DoesNotContain("private-restarted", fact.GetRawText(), StringComparison.Ordinal);
                    break;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [PostgresFact]
    public async Task RejectedPasswords_CommitFailureAndLockoutFacts_ButLockedLoginDoesNot()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        var request = new { userName = "audit-lockout-user", password = "private-correct-password" };
        using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var rejected = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
                new { request.userName, password = "private-wrong-password" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var locked = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = new EfOutboxStore<IdentityDbContext>(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
        var facts = (await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow))
            .Select(entry => JsonSerializer.Deserialize<JsonElement>(entry.Payload)).OrderBy(fact => fact.GetProperty("version").GetInt64()).ToArray();
        Assert.Equal(6, facts.Length);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, facts.Select(fact => fact.GetProperty("version").GetInt64()));
        Assert.Equal(new[] { "created", "login-failed", "login-failed", "login-failed", "login-failed", "login-locked" },
            facts.Select(fact => fact.GetProperty("operation").GetString()));
        Assert.All(facts, fact =>
        {
            Assert.Equal(JsonValueKind.Null, fact.GetProperty("actorId").ValueKind);
            Assert.DoesNotContain("password", fact.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(request.userName, fact.GetRawText(), StringComparison.Ordinal);
        });
    }

    [PostgresFact]
    public async Task Registration_CommitsOneMinimalFact_AndDuplicateDoesNotInventAnother()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "identity-registration-audit");
        var request = new { userName = "private-registration-name", password = "private-registration-password" };
        using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var user = await registered.Content.ReadApiDataAsync();
        var id = user.GetProperty("userId").ReadHttpInt64().ToString(CultureInfo.InvariantCulture);

        await using var scope = app.Services.CreateAsyncScope();
        var outbox = new EfOutboxStore<IdentityDbContext>(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
        var entry = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
        Assert.Equal("identity.entity-committed.v1", entry.EventName);
        using var payload = JsonDocument.Parse(entry.Payload);
        var fact = payload.RootElement;
        Assert.Equal("user", fact.GetProperty("subjectType").GetString());
        Assert.Equal(id, fact.GetProperty("subjectId").GetString());
        Assert.Equal("created", fact.GetProperty("operation").GetString());
        Assert.Equal(1, fact.GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("actorId").ValueKind);
        Assert.Equal("identity-registration-audit", fact.GetProperty("correlationId").GetString());
        Assert.NotEqual(Guid.Empty, fact.GetProperty("execution").GetProperty("operationId").GetGuid());
        Assert.DoesNotContain(request.userName, entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(request.password, entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", entry.Payload, StringComparison.OrdinalIgnoreCase);

        using var duplicate = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(entry.Id, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow)).Id);
    }
}
