using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CentralAuditCapacityHttpTests
{
    [PostgresFact]
    public async Task BrokenCentralLedger_ReturnsUnavailable_AndBusinessReadinessRecoversIndependently()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "central-capacity-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "central-capacity-root-password");
        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var remove = new NpgsqlCommand("DELETE FROM auditing.central_storage_capacity WHERE \"Pool\" = 'observations'", fault))
        { await remove.ExecuteNonQueryAsync(); }
        using var unavailable = await client.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var diagnostic = await unavailable.Content.ReadAsStringAsync();
        Assert.Contains("auditing.storage_unavailable", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", diagnostic, StringComparison.Ordinal);
        using var logging = await client.GetAsync(new Uri("/health/logging", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, logging.StatusCode);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using (var repair = new NpgsqlCommand("INSERT INTO auditing.central_storage_capacity VALUES ('observations', 0)", fault))
        { await repair.ExecuteNonQueryAsync(); }
        using var recovered = await client.GetAsync(new Uri("/api/auditing/capacity", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ExhaustedCentralCapacity_DegradesLoggingWithoutRemovingBusinessReadiness()
    {
        await using var app = new CapacityApp();
        using var client = app.CreateClient();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
            for (var index = 0; index < 2; index++)
            {
                Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(new AuditFact(Guid.NewGuid(),
                    "platform.setting-committed.v1", "platform", "platform.setting.changed", "global-setting", "1", 2,
                    "actor", DateTimeOffset.UtcNow, "trace", "correlation"))).Value);
            }
        }
        using var logging = await client.GetAsync(new Uri("/health/logging", UriKind.Relative));
        Assert.Equal("Degraded", await logging.Content.ReadAsStringAsync());
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Capacity_RequiresItsOwnPermission_AndCountsRecordsOutsideInvestigationWindow()
    {
        await using var app = new CapacityApp();
        using var client = app.CreateClient();
        var path = new Uri("/api/auditing/capacity", UriKind.Relative);
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "capacity-reader", password = "capacity-reader-password" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(client, "capacity-reader", "capacity-reader-password");
        using var forbidden = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var now = DateTimeOffset.UtcNow;
            var fact = new AuditFact(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
                "global-setting", "1", 2, "actor", now.AddDays(-30), "trace", "correlation");
            Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact)).Value);
        }
        using var allowed = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var data = await allowed.Content.ReadApiDataAsync();
        Assert.Equal(1, data.GetProperty("facts").GetProperty("records").ReadHttpInt64());
        Assert.Equal(2, data.GetProperty("facts").GetProperty("instanceLimit").ReadHttpInt64());
        Assert.Equal(1, data.GetProperty("facts").GetProperty("available").ReadHttpInt64());
        Assert.Equal(0, data.GetProperty("observations").GetProperty("records").ReadHttpInt64());
        Assert.Equal(3, data.GetProperty("observations").GetProperty("instanceLimit").ReadHttpInt64());
        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Contains("/api/auditing/capacity", await openApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var write = await client.PutAsJsonAsync(path, new { maxFacts = 100 });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
    }

    [Theory]
    [InlineData("MaxFacts", "0")]
    [InlineData("MaxFacts", "1000000001")]
    [InlineData("MaxObservations", "0")]
    [InlineData("MaxObservations", "1000000001")]
    [InlineData("WaitTimeoutMilliseconds", "49")]
    [InlineData("WaitTimeoutMilliseconds", "30001")]
    public async Task InvalidCapacity_RefusesHostComposition(string key, string value)
    {
        await using var app = new CapacityApp(key, value);
        var error = Assert.ThrowsAny<ArgumentException>(() => app.CreateClient());
        Assert.Contains("Auditing:Capacity", error.Message, StringComparison.Ordinal);
    }

    private sealed class CapacityApp(string? key = null, string? value = null) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Auditing:Capacity:MaxFacts"] = "2",
                ["Auditing:Capacity:MaxObservations"] = "3",
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
            };
            if (key is not null) { settings["Auditing:Capacity:" + key] = value; }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }
    }
}
