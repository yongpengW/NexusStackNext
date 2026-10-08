using System.Net;
using System.Net.Http.Json;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class UserLifecyclePersistenceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgreSQL_directory_returns_bounded_safe_projections_and_assigned_roles()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "lifecycle-root-password", schedulingWorkerEnabled: false);
        using var admin = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(admin, "journey-root", "lifecycle-root-password");
        using var created = await admin.PostAsJsonAsync(Path("/api/identity/users"), new { userName = "postgres-directory", password = "directory-user-password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        using var role = await admin.PostAsJsonAsync(Path("/api/identity/roles"), new { code = "directory-role", name = "Directory role" });
        var roleId = (await role.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
        using var assigned = await admin.PostAsync(Path($"/api/identity/users/{id}/roles/{roleId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        using var page = await admin.GetAsync(Path("/api/identity/users?limit=1"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var data = await page.Content.ReadApiDataAsync();
        Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.True(data.GetProperty("nextAfterUserId").ReadHttpInt64() > 0);
        using var detail = await admin.GetAsync(Path($"/api/identity/users/{id}"));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var user = await detail.Content.ReadApiDataAsync();
        Assert.Equal("postgres-directory", user.GetProperty("userName").GetString());
        Assert.Equal(roleId, Assert.Single(user.GetProperty("roleIds").EnumerateArray()).ReadHttpInt64());
        Assert.Equal(2, user.GetProperty("version").ReadHttpInt64());
    }

    private static Uri Path(string value) => new(value, UriKind.Relative);

    [PostgresFact]
    public async Task Failed_database_commit_preserves_user_password_session_and_committed_audit_facts()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "lifecycle-root-password", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        using var ordinary = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "lifecycle-root-password");
        var id = await CreateUserAsync(root, "failed-lifecycle");
        var tokens = await UserLifecycleHttpTests.LoginAsync(ordinary, "failed-lifecycle", "lifecycle-user-password");
        var before = await RetainedFactsAsync(root);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("""
            CREATE FUNCTION identity.reject_lifecycle_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."IsEnabled" IS DISTINCT FROM OLD."IsEnabled" OR NEW."PasswordHash" IS DISTINCT FROM OLD."PasswordHash" THEN
                RAISE EXCEPTION 'injected lifecycle commit failure';
              END IF;
              RETURN NEW;
            END; $$;
            CREATE TRIGGER reject_lifecycle_write BEFORE UPDATE ON identity.users FOR EACH ROW EXECUTE FUNCTION identity.reject_lifecycle_write();
            """, connection)) { await command.ExecuteNonQueryAsync(); }
        foreach (var suffix in new[] { "disable", "password" })
        {
            using var failed = await root.PostAsJsonAsync(Path($"/api/identity/users/{id}/{suffix}"), new { expectedVersion = 2, newPassword = "failed-new-password" });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            using var detail = await root.GetAsync(Path($"/api/identity/users/{id}"));
            var current = await detail.Content.ReadApiDataAsync();
            Assert.True(current.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(2, current.GetProperty("version").ReadHttpInt64());
            using var mine = await ordinary.GetAsync(Path("/api/identity/me"));
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            Assert.Equal(before, await RetainedFactsAsync(root));
        }
        await using (var command = new NpgsqlCommand("DROP TRIGGER reject_lifecycle_write ON identity.users; DROP FUNCTION identity.reject_lifecycle_write();", connection))
        { await command.ExecuteNonQueryAsync(); }
        using var refreshed = await ordinary.PostAsJsonAsync(Path("/api/identity/refresh"), new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        await UserLifecycleHttpTests.LoginAsync(ordinary, "failed-lifecycle", "lifecycle-user-password");
    }

    [PostgresFact]
    public async Task Ordinary_user_occupying_the_configured_root_name_causes_a_stable_startup_failure()
    {
        await using var database = await databases.CreateAsync();
        await using (var first = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false))
        {
            using var client = first.CreateClient();
            await CreateUserAsync(client, "journey-root");
        }
        await using (var collided = new PersistentIdentityApp(database.ConnectionString, "lifecycle-root-password", schedulingWorkerEnabled: false))
        {
            var failure = Assert.ThrowsAny<Exception>(() => collided.CreateClient());
            Assert.Contains("identity.root.name_collision", failure.ToString(), StringComparison.Ordinal);
        }
        await using var recovered = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var observer = recovered.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(observer, "journey-root", "lifecycle-user-password");
        using var denied = await observer.GetAsync(Path("/api/identity/users"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static async Task<long> RetainedFactsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Path("/api/identity/audit-capacity"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64();
    }

    [PostgresFact]
    public async Task Two_authorized_administrators_racing_on_one_user_commit_one_rotation_and_return_conflict()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "lifecycle-root-password", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        using var delegated = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(root, "journey-root", "lifecycle-root-password");
        var administrator = await CreateUserAsync(root, "delegated-admin");
        var target = await CreateUserAsync(root, "competing-target");
        using var menu = await root.PostAsJsonAsync(Path("/api/identity/menus"), new { title = "User management", sortOrder = 1 });
        var menuId = (await menu.Content.ReadApiDataAsync()).GetProperty("menuId").ReadHttpInt64();
        using var resource = await root.PostAsJsonAsync(Path("/api/identity/api-resources"), new { path = "/api/identity/users/{userId}/password", method = "POST", menuId });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        using var role = await root.PostAsJsonAsync(Path("/api/identity/roles"), new { code = "user-manager", name = "User manager" });
        var roleId = (await role.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();
        using var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        using var assigned = await root.PostAsync(Path($"/api/identity/users/{administrator}/roles/{roleId}"), null);
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        await UserLifecycleHttpTests.LoginAsync(delegated, "delegated-admin", "lifecycle-user-password");

        await using var barrier = new NpgsqlConnection(database.ConnectionString);
        await barrier.OpenAsync();
        await using var locked = await barrier.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM identity.users WHERE \"Id\" = @id FOR UPDATE", barrier, locked))
        { command.Parameters.AddWithValue("id", target); await command.ExecuteScalarAsync(); }
        // pg_stat_activity 在显式事务中可能沿用首次快照；独立自动提交观察者每轮取新状态。
        await using var observation = new NpgsqlConnection(database.ConnectionString);
        await observation.OpenAsync();
        await using (var initial = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> @barrier", observation))
        {
            initial.Parameters.AddWithValue("barrier", barrier.ProcessID);
            Assert.Equal(0L, (long)(await initial.ExecuteScalarAsync())!);
        }
        var first = root.PostAsJsonAsync(Path($"/api/identity/users/{target}/password"), new { expectedVersion = 1, newPassword = "first-admin-password" });
        var second = delegated.PostAsJsonAsync(Path($"/api/identity/users/{target}/password"), new { expectedVersion = 1, newPassword = "second-admin-password" });
        try
        {
            using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                Assert.False(first.IsCompleted, "First rotation ended before both writes reached the lock.");
                Assert.False(second.IsCompleted, "Second rotation ended before both writes reached the lock.");
                await using var probe = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> @barrier", observation);
                probe.Parameters.AddWithValue("barrier", barrier.ProcessID);
                if ((long)(await probe.ExecuteScalarAsync(waiting.Token))! >= 2) { break; }
                await Task.Delay(20, waiting.Token);
            }
        }
        finally { await locked.RollbackAsync(); }
        using var firstResult = await first;
        using var secondResult = await second;
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], new[] { firstResult.StatusCode, secondResult.StatusCode }.Order());
        using var detail = await root.GetAsync(Path($"/api/identity/users/{target}"));
        Assert.Equal(2, (await detail.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64());
        using var observer = app.CreateClient();
        await UserLifecycleHttpTests.LoginAsync(observer, "competing-target", firstResult.StatusCode == HttpStatusCode.NoContent ? "first-admin-password" : "second-admin-password");
    }

    private static async Task<long> CreateUserAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(Path("/api/identity/users"), new { userName = name, password = "lifecycle-user-password" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
    }
}
