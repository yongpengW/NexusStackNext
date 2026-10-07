using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class BusinessFactCapacityDiagnosticsTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task BusinessSources_RejectInvalidReadBudgetsBeforeStarting()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        foreach (var owner in new[] { "Costing", "Pricing" })
        {
            var assembly = owner == "Costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
            if (owner == "Costing") { await JourneyDatabaseOperation.RunAsync(() => CostingDatabase.MigrateAsync(database.ConnectionString)); }
            else { await JourneyDatabaseOperation.RunAsync(() => PricingDatabase.MigrateAsync(database.ConnectionString)); }
            // 同一数据库和其余配置先实际启动，排除未迁移等其他拒绝原因；宿主错误文本刻意脱敏。
            await using (var valid = await BusinessProcess.StartAsync(assembly, owner, database.ConnectionString))
            {
                valid.Authenticate();
                using var response = await valid.Client.GetAsync(new Uri($"/api/{owner.ToLowerInvariant()}/audit-capacity", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            var start = BusinessProcess.StartInfo(assembly, owner, database.ConnectionString);
            start.Environment[$"{owner}__AuditDelivery__CapacityRead__Timeout"] = "00:00:30.001";
            var result = await BusinessProcess.RunToExitAsync(start);
            Assert.Equal(1, result.ExitCode);
        }
    }

    [PostgresFact]
    public async Task BusinessSources_ReportMissingLedgerAsUnavailable_AndRecoverWithoutRestart()
    {
        foreach (var owner in new[] { "Costing", "Pricing" })
        {
            await using var database = await databases.CreateAsync(owner.ToLowerInvariant() + "-only");
            var assembly = owner == "Costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
            await using var app = await BusinessProcess.StartAsync(assembly, owner, database.ConnectionString);
            app.Authenticate();
            var schema = owner.ToLowerInvariant();
            var path = new Uri($"/api/{schema}/audit-capacity", UriKind.Relative);
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var hide = new NpgsqlCommand($"ALTER TABLE {schema}.fact_capacity RENAME TO unavailable_capacity", connection))
            { await hide.ExecuteNonQueryAsync(); }
            try
            {
                using var unavailable = await app.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                var error = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("audit_capacity.unavailable", error.GetProperty("errorCode").GetString());
                Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
            }
            finally
            {
                await using var restore = new NpgsqlCommand($"ALTER TABLE {schema}.unavailable_capacity RENAME TO fact_capacity", connection);
                await restore.ExecuteNonQueryAsync();
            }
            using var recovered = await app.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(0, (await recovered.Content.ReadApiDataAsync()).GetProperty("retainedRecords").ReadHttpInt64());
        }
    }

    [PostgresFact]
    public async Task Pricing_OnlyOperatorCanReadCommittedCapacity_AndReadDoesNotAcceptMoreWork()
    {
        await using var database = await databases.CreateAsync("pricing-only");
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString);
        var path = new Uri("/api/pricing/audit-capacity", UriKind.Relative);
        using var anonymous = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        app.Authenticate(root: false);
        using var ordinary = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
        app.Authenticate();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), new
        {
            requestId = Guid.NewGuid(),
            itemId = Guid.NewGuid(),
            expectedVersion = "0",
            unitCost = 80m,
            feeRate = 0.2m,
        });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await AssertCommittedCapacityAsync(app.Client, "pricing");
    }

    [PostgresFact]
    public async Task Costing_OnlyOperatorCanReadCommittedCapacity_AndReadDoesNotAcceptMoreWork()
    {
        await using var database = await databases.CreateAsync("costing-only");
        await using var app = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        var path = new Uri("/api/costing/audit-capacity", UriKind.Relative);
        using var anonymous = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        app.Authenticate(root: false);
        using var ordinary = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
        app.Authenticate();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), new
        {
            requestId = Guid.NewGuid(),
            itemId = Guid.NewGuid(),
            expectedVersion = "0",
            purchaseCost = 80m,
            freightCost = 20m,
        });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await AssertCommittedCapacityAsync(app.Client, "costing");
    }

    private static async Task AssertCommittedCapacityAsync(HttpClient client, string owner)
    {
        var path = new Uri($"/api/{owner}/audit-capacity", UriKind.Relative);
        using var read = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var snapshot = await read.Content.ReadApiDataAsync();
        Assert.Equal(owner, snapshot.GetProperty("context").GetString());
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(1, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(99999, snapshot.GetProperty("remainingRecords").ReadHttpInt64());
        Assert.InRange(snapshot.GetProperty("retainedPayloadBytes").ReadHttpInt64(), 1, 1048576);
        using var repeated = await client.GetAsync(path);
        Assert.Equal(snapshot.GetRawText(), (await repeated.Content.ReadApiDataAsync()).GetRawText());
        FactCapacityAccessTests.AssertCapacitySchema(await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative)), path.OriginalString);
    }
}
