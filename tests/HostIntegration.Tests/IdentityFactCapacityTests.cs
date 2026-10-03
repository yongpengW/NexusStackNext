using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityFactCapacityTests
{
    [PostgresFact]
    public async Task MenuCreation_RequiresCapacityForItsEntireFactBatch_AndNextRequestCanRecover()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "capacity-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-root-password");
        var before = await ReadPendingIdsAsync(app.Services);
        Assert.NotEmpty(before);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        long firstLimit;
        await using (var policy = new NpgsqlCommand("UPDATE identity.fact_capacity SET \"MaxRecords\" = \"RetainedRecords\" + 1 RETURNING \"MaxRecords\"", connection))
        {
            firstLimit = Assert.IsType<long>(await policy.ExecuteScalarAsync());
        }
        using var refused = await client.PostAsJsonAsync(new Uri("/api/identity/menus", UriKind.Relative), new { title = "private rejected title", sortOrder = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var error = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("identity.audit_capacity.exhausted", error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private rejected title", error.GetRawText(), StringComparison.Ordinal);
        using var missing = await client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
        Assert.Empty((await missing.Content.ReadApiDataAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(before, await ReadPendingIdsAsync(app.Services));

        await using (var policy = new NpgsqlCommand("UPDATE identity.fact_capacity SET \"MaxRecords\" = @limit", connection))
        {
            // Use the pre-failure limit: a leaked first reservation must make this retry fail.
            policy.Parameters.AddWithValue("limit", firstLimit + 1);
            Assert.Equal(1, await policy.ExecuteNonQueryAsync());
        }
        using var accepted = await client.PostAsJsonAsync(new Uri("/api/identity/menus", UriKind.Relative), new { title = "recovered", sortOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var menus = await client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
        var menu = Assert.Single((await menus.Content.ReadApiDataAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal("recovered", menu.GetProperty("title").GetString());
        Assert.Equal(2, (await ReadPendingIdsAsync(app.Services)).Except(before).Count());
    }

    private static async Task<Guid[]> ReadPendingIdsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity").ReadPendingAsync(100, DateTimeOffset.UtcNow))
            .Select(entry => entry.Id).Order().ToArray();
    }
}
