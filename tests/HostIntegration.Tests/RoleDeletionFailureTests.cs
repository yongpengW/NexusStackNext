using System.Net;
using System.Net.Http.Json;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class RoleDeletionFailureTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Failed_role_delete_does_not_leave_a_deleted_or_unusable_role()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "role-delete-failure-root", schedulingWorkerEnabled: false);
        using var root = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "role-delete-failure-root");
        using var created = await root.PostAsJsonAsync(new Uri("/api/identity/roles", UriKind.Relative),
            new { code = "delete-failure-role", name = "Delete failure" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var roleId = (await created.Content.ReadApiDataAsync()).GetProperty("roleId").ReadHttpInt64();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION identity.reject_role_delete_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected role delete fact failure'; END; $$;
            CREATE TRIGGER reject_role_delete_fact BEFORE INSERT ON identity.outbox FOR EACH ROW EXECUTE FUNCTION identity.reject_role_delete_fact();
            """, connection))
        { await inject.ExecuteNonQueryAsync(); }

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/identity/roles/{roleId}", UriKind.Relative))
        { Content = JsonContent.Create(new { expectedVersion = 1 }) };
        using var failed = await root.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var retained = await ReadAsync(root, $"/api/identity/roles/{roleId}");
        Assert.Equal(1, retained.GetProperty("version").ReadHttpInt64());
        Assert.True(retained.GetProperty("isEnabled").GetBoolean());
    }

    private static async Task<System.Text.Json.JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }
}
