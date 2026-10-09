using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class BusinessAuthorizationPersistenceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Dangling_menu_and_role_references_cannot_authorize_even_when_the_diagnostic_cache_is_hot()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "access-root-password", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await LoginRootAsync(root);
        var (userId, roleId, menuId) = await GrantReaderAsync(root, user);
        Assert.True((await app.Services.GetRequiredService<IPermissionCache>().GetAsync(new UserId(userId))).Value.Contains("/api/identity/menus:GET"));
        using (var granted = await user.GetAsync(Path("/api/identity/menus"))) { Assert.Equal(HttpStatusCode.OK, granted.StatusCode); }
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // Arrange broken references at the storage boundary; directory deletion HTTP is deliberately outside this ticket.
        foreach (var sql in new[]
        {
            "UPDATE identity.role_menus SET menu_id = 900099 WHERE role_id = @role",
            "UPDATE identity.role_menus SET menu_id = @menu WHERE role_id = @role; DELETE FROM identity.roles WHERE \"Id\" = @role",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("role", roleId);
            command.Parameters.AddWithValue("menu", menuId);
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
            using var denied = await user.GetAsync(Path("/api/identity/menus"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Remote_revocation_and_a_late_authority_response_cannot_restore_access_from_a_hot_permission_cache()
    {
        await using var database = await databases.CreateAsync();
        await using var first = new PersistentIdentityApp(database.ConnectionString, "access-root-password", schedulingWorkerEnabled: false);
        await using var second = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var root = first.CreateClient();
        using var user = first.CreateClient();
        using var other = second.CreateClient();
        await LoginRootAsync(root);
        var (userId, roleId, _) = await GrantReaderAsync(root, user);
        other.DefaultRequestHeaders.Authorization = user.DefaultRequestHeaders.Authorization;
        var cache = first.Services.GetRequiredService<IPermissionCache>();
        Assert.True((await cache.GetAsync(new UserId(userId))).Value.Contains(NexusStackNext.BuildingBlocks.Domain.Authorization.PermissionKey.From("/api/identity/menus", "GET")));
        await using var barrier = new NpgsqlConnection(database.ConnectionString);
        await barrier.OpenAsync();
        await using var locked = await barrier.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE identity.api_resources IN ACCESS EXCLUSIVE MODE", barrier, locked)) { await command.ExecuteNonQueryAsync(); }
        var oldRead = user.GetAsync(Path("/api/identity/access/v1?permissionKey=%2Fapi%2Fidentity%2Fmenus%3AGET"));
        try
        {
            await WaitForBlockedAsync(database.ConnectionString, barrier.ProcessID, 1);
            await using var scope = second.Services.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var current = await sender.QueryAsync(new GetUserQuery(userId));
            Assert.True(current.IsSuccess);
            Assert.True((await sender.SendAsync(new RevokeRoleCommand(userId, roleId, current.Value.Version))).IsSuccess);
        }
        finally { await locked.RollbackAsync(); }
        using (var late = await oldRead) { Assert.Contains(late.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }); }
        // Another Identity instance cannot evict this instance's diagnostic cache; admission still reads committed authority.
        Assert.True((await cache.GetAsync(new UserId(userId))).Value.Contains(NexusStackNext.BuildingBlocks.Domain.Authorization.PermissionKey.From("/api/identity/menus", "GET")));
        foreach (var client in new[] { user, other })
        {
            using var denied = await client.GetAsync(Path("/api/identity/menus"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var stillAuthenticated = await client.GetAsync(Path("/api/identity/me"));
            Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Failed_user_or_role_commit_preserves_permissions_versions_and_committed_facts()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "access-root-password", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        using var user = app.CreateClient();
        await LoginRootAsync(root);
        var (userId, roleId, _) = await GrantReaderAsync(root, user);
        var userBefore = await ReadAsync(root, $"/api/identity/users/{userId}");
        var before = (await ReadAsync(root, "/api/identity/audit-capacity")).GetProperty("retainedRecords").ReadHttpInt64();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("""
            CREATE FUNCTION identity.reject_permission_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected permission commit failure'; END; $$;
            CREATE TRIGGER reject_permission_write BEFORE UPDATE ON identity.roles FOR EACH ROW EXECUTE FUNCTION identity.reject_permission_write();
            CREATE TRIGGER reject_permission_write BEFORE UPDATE ON identity.users FOR EACH ROW EXECUTE FUNCTION identity.reject_permission_write();
            """, connection)) { await command.ExecuteNonQueryAsync(); }
        using (var failed = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = "2", menuIds = Array.Empty<string>() })) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        using (var failed = await root.PostAsJsonAsync(Path($"/api/identity/users/{userId}/roles/{roleId}/revoke"), new { expectedVersion = userBefore.GetProperty("version").ReadHttpInt64() })) { Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); }
        Assert.Equal(2, (await ReadAsync(root, $"/api/identity/roles/{roleId}")).GetProperty("version").ReadHttpInt64());
        Assert.Equal(userBefore.GetProperty("version").ReadHttpInt64(), (await ReadAsync(root, $"/api/identity/users/{userId}")).GetProperty("version").ReadHttpInt64());
        Assert.Equal(before, (await ReadAsync(root, "/api/identity/audit-capacity")).GetProperty("retainedRecords").ReadHttpInt64());
        using var retained = await user.GetAsync(Path("/api/identity/menus"));
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
    }

    [PostgresFact]
    public async Task Root_and_delegated_permission_manager_racing_to_replace_one_role_commit_once_and_return_conflict()
    {
        await using var database = await databases.CreateAsync();
        var connections = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString;
        await using var app = new PersistentIdentityApp(connections, "access-root-password", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        using var delegated = app.CreateClient();
        await LoginRootAsync(root);
        var (_, targetRole, targetMenu) = await GrantReaderAsync(root, delegated);
        var managerMenu = (await CreateAsync(root, "/api/identity/menus", new { title = "Permission management", sortOrder = 2 })).GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/roles/{roleId}/menus", method = "PUT", menuId = managerMenu });
        var managerRole = (await CreateAsync(root, "/api/identity/roles", new { code = "permission-manager", name = "Permission manager" })).GetProperty("roleId").ReadHttpInt64();
        using (var granted = await root.PostAsync(Path($"/api/identity/roles/{managerRole}/menus/{managerMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
        var subject = (await ReadAsync(delegated, "/api/identity/me")).GetProperty("userId").ReadHttpInt64();
        using (var assigned = await root.PostAsync(Path($"/api/identity/users/{subject}/roles/{managerRole}"), null)) { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
        // Business authorization cannot register resources, even for a delegated permission manager.
        using (var escalation = await delegated.PostAsJsonAsync(Path("/api/identity/api-resources"), new { path = "/api/identity/users", method = "GET", menuId = targetMenu })) { Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode); }
        await using var barrier = new NpgsqlConnection(database.ConnectionString);
        await barrier.OpenAsync();
        await using var locked = await barrier.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM identity.roles WHERE \"Id\" = @id FOR UPDATE", barrier, locked))
        { command.Parameters.AddWithValue("id", targetRole); await command.ExecuteScalarAsync(); }
        var first = root.PutAsJsonAsync(Path($"/api/identity/roles/{targetRole}/menus"), new { expectedVersion = "2", menuIds = Array.Empty<long>() });
        var second = delegated.PutAsJsonAsync(Path($"/api/identity/roles/{targetRole}/menus"), new { expectedVersion = "2", menuIds = new[] { managerMenu } });
        try { await WaitForBlockedAsync(database.ConnectionString, barrier.ProcessID, 2); }
        finally { await locked.RollbackAsync(); }
        using var a = await first;
        using var b = await second;
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], new[] { a.StatusCode, b.StatusCode }.Order());
        var current = await ReadAsync(root, $"/api/identity/roles/{targetRole}");
        Assert.Equal(3, current.GetProperty("version").ReadHttpInt64());
        Assert.Equal(a.StatusCode == HttpStatusCode.NoContent ? Array.Empty<long>() : new[] { managerMenu }, current.GetProperty("menuIds").EnumerateArray().Select(value => value.ReadHttpInt64()));
    }

    private static async Task WaitForBlockedAsync(string connectionString, int barrier, int minimum)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            // Role child-row changes may block the second writer on the first writer before its aggregate UPDATE.
            await using var probe = new NpgsqlCommand("""
                WITH RECURSIVE blocked AS (
                    SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid))
                    UNION
                    SELECT a.pid FROM pg_stat_activity a JOIN blocked b ON b.pid = ANY(pg_blocking_pids(a.pid)) WHERE a.datname = current_database()
                ) SELECT count(*) FROM blocked
                """, observer);
            probe.Parameters.AddWithValue("barrier", barrier);
            if ((long)(await probe.ExecuteScalarAsync(timeout.Token))! >= minimum) { return; }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<(long UserId, long RoleId, long MenuId)> GrantReaderAsync(HttpClient root, HttpClient user)
    {
        var userId = (await CreateAsync(root, "/api/identity/users", new { userName = "persisted-reader", password = "persisted-reader-password" })).GetProperty("userId").ReadHttpInt64();
        var menuId = (await CreateAsync(root, "/api/identity/menus", new { title = "Read menus", sortOrder = 1 })).GetProperty("menuId").ReadHttpInt64();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/identity/menus", method = "GET", menuId });
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "persisted-reader", name = "Reader" })).GetProperty("roleId").ReadHttpInt64();
        using (var grant = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{menuId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using (var assign = await root.PostAsync(Path($"/api/identity/users/{userId}/roles/{roleId}"), null)) { Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode); }
        await UserLifecycleHttpTests.LoginAsync(user, "persisted-reader", "persisted-reader-password");
        return (userId, roleId, menuId);
    }

    private static Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T body) => ReadAsync(client, path, body);
    private static Task<JsonElement> ReadAsync(HttpClient client, string path) => ReadAsync<object>(client, path, null);
    private static async Task<JsonElement> ReadAsync<T>(HttpClient client, string path, T? body)
    {
        using var response = body is null ? await client.GetAsync(Path(path)) : await client.PostAsJsonAsync(Path(path), body);
        Assert.Equal(body is null ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
    private static Task<JsonElement> LoginRootAsync(HttpClient root) => UserLifecycleHttpTests.LoginAsync(root, "journey-root", "access-root-password");
    private static Uri Path(string path) => new(path, UriKind.Relative);
}
