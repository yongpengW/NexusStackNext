using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditExportJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Gateway_checks_independent_export_permission_current_revocation_and_exact_owner_on_all_entries()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var platform = new AuditExportApp(database.ConnectionString, certificates);
        using var backend = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(backend.BaseAddress!.AbsoluteUri)
        {
            SigningKey = PersistentIdentityApp.SigningKey,
            SessionAuthorityAddress = backend.BaseAddress.AbsoluteUri,
            RateLimitPermitLimit = 1000,
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var root = gateway.CreateClient();
        using var owner = gateway.CreateClient();
        using var other = gateway.CreateClient();
        using var anonymous = gateway.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "audit-export-password");
        var ownerId = (await CreateAsync(root, "/api/identity/users", new { userName = "export-owner", password = "export-owner-password" })).GetProperty("userId").GetString();
        var otherId = (await CreateAsync(root, "/api/identity/users", new { userName = "export-other", password = "export-other-password" })).GetProperty("userId").GetString();
        var readMenu = (await CreateAsync(root, "/api/identity/menus", new { title = "Read audit", sortOrder = 1 })).GetProperty("menuId").GetString();
        var exportMenu = (await CreateAsync(root, "/api/identity/menus", new { title = "Export audit", sortOrder = 2 })).GetProperty("menuId").GetString();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/auditing/entries", method = "GET", menuId = readMenu });
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/auditing/exports", method = "POST", menuId = exportMenu });
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "audit-export-role", name = "Audit export role" })).GetProperty("roleId").GetString();
        using (var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{readMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        foreach (var userId in new[] { ownerId, otherId })
        {
            using var assigned = await root.PostAsync(Relative($"/api/identity/users/{userId}/roles/{roleId}"), null);
            Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        }
        await PlatformSettingsAccessTests.LoginAsync(owner, "export-owner", "export-owner-password");
        await PlatformSettingsAccessTests.LoginAsync(other, "export-other", "export-other-password");
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(new AuditFact(Guid.NewGuid(),
                "platform.setting-committed.v1", "platform", "platform.setting.created", "global-setting", "export.access", 1,
                "42", DateTimeOffset.UtcNow, "access-trace", "access-correlation"))).IsSuccess);
        }
        var request = new { requestId = Guid.NewGuid(), facts = new { subjectId = "export.access" } };
        using (var unsigned = await anonymous.PostAsJsonAsync(Relative("/api/auditing/exports"), request)) { Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode); }
        using (var read = await owner.GetAsync(Relative("/api/auditing/entries"))) { Assert.Equal(HttpStatusCode.OK, read.StatusCode); }
        using (var denied = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{exportMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using var accepted = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var status = await accepted.Content.ReadApiDataAsync();
        var id = status.GetProperty("exportId").GetGuid();
        foreach (var foreign in new[] { root, other })
        {
            using var detail = await foreign.GetAsync(Relative($"/api/auditing/exports/{id}"));
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            using var artifact = await foreign.GetAsync(Relative($"/api/auditing/exports/{id}/artifact"));
            Assert.Equal(HttpStatusCode.NotFound, artifact.StatusCode);
            using var cancel = await foreign.PostAsJsonAsync(Relative($"/api/auditing/exports/{id}/cancel"), new { expectedVersion = status.GetProperty("version").GetString() });
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
            using var center = await foreign.GetAsync(Relative("/api/auditing/exports"));
            Assert.Empty((await center.Content.ReadApiDataAsync()).EnumerateArray());
        }
        using (var role = await root.GetAsync(Relative($"/api/identity/roles/{roleId}")))
        {
            var version = (await role.Content.ReadApiDataAsync()).GetProperty("version").GetString();
            using var revoke = await root.PutAsJsonAsync(Relative($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = version, menuIds = new[] { readMenu } });
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }
        foreach (var path in new[] { "/api/auditing/exports", $"/api/auditing/exports/{id}", $"/api/auditing/exports/{id}/artifact" })
        {
            using var denied = await owner.GetAsync(Relative(path));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var denied = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request)) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        foreach (var action in new[] { "cancel", "retry" })
        {
            using var denied = await owner.PostAsJsonAsync(Relative($"/api/auditing/exports/{id}/{action}"), new { expectedVersion = status.GetProperty("version").GetString() });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var grant = await root.PostAsync(Relative($"/api/identity/roles/{roleId}/menus/{exportMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode); }
        using var replay = await owner.PostAsJsonAsync(Relative("/api/auditing/exports"), request);
        Assert.Equal(id, (await replay.Content.ReadApiDataAsync()).GetProperty("exportId").GetGuid());
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(Relative(path), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    [PostgresFact]
    public async Task Unknown_or_duplicate_filters_and_oversized_requests_are_rejected_before_snapshot_creation()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var platform = new AuditExportApp(database.ConnectionString, certificates);
        using var client = platform.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-export-password");
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(new AuditFact(Guid.NewGuid(),
                "platform.setting-committed.v1", "platform", "platform.setting.created", "global-setting", "export.invalid", 1,
                "42", DateTimeOffset.UtcNow, "invalid-trace", "invalid-correlation"))).IsSuccess);
        }
        var id = Guid.NewGuid().ToString("D");
        foreach (var json in new[]
        {
            $$$"""{"requestId":"{{{id}}}","facts":{"surce":"platform"}}""",
            $$$"""{"requestId":"{{{id}}}","ownerId":"another-owner","facts":{}}""",
            $$$"""{"requestId":"{{{id}}}","facts":{"source":"pricing","source":"platform"}}""",
            $$$"""{"requestId":"{{{id}}}","requestId":"{{{id}}}","facts":{}}""",
        })
        {
            using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri("/api/auditing/exports", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var oversized = new StringContent(new string(' ', 16_385), System.Text.Encoding.UTF8, "application/json");
        using var rejected = await client.PostAsync(new Uri("/api/auditing/exports", UriKind.Relative), oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        using var center = await client.GetAsync(new Uri("/api/auditing/exports", UriKind.Relative));
        Assert.Empty((await center.Content.ReadApiDataAsync()).EnumerateArray());
    }

    [PostgresFact]
    public async Task Owner_download_center_returns_safe_metadata_and_cancel_is_idempotent_before_publication()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var platform = new AuditExportApp(database.ConnectionString, certificates);
        using var client = platform.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-export-password");
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(new AuditFact(Guid.NewGuid(),
                "platform.setting-committed.v1", "platform", "platform.setting.created", "global-setting", "export.center", 1,
                "42", DateTimeOffset.UtcNow, "center-trace", "center-correlation"))).IsSuccess);
        }
        await using var migration = new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, AuditingDbContext.SchemaName).Options);
        var migrator = migration.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(async () =>
        {
            await migrator.MigrateAsync("20261004171835_CapacityPolicyAuditEvidence");
            await migrator.MigrateAsync();
        });
        using var accepted = await client.PostAsJsonAsync(new Uri("/api/auditing/exports", UriKind.Relative),
            new { requestId = Guid.NewGuid(), facts = new { subjectId = "export.center" } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var status = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var id = status.GetProperty("exportId").GetGuid();
        using var found = await client.GetAsync(new Uri($"/api/auditing/exports/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var metadata = (await found.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(metadata.TryGetProperty("rows", out _));
        Assert.NotEqual(default, metadata.GetProperty("audit").GetProperty("createdAt").GetDateTimeOffset());
        var version = metadata.GetProperty("version").GetString();
        using var cancelled = await client.PostAsJsonAsync(new Uri($"/api/auditing/exports/{id}/cancel", UriKind.Relative), new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var result = (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Cancelled", result.GetProperty("state").GetString());
        using var repeated = await client.PostAsJsonAsync(new Uri($"/api/auditing/exports/{id}/cancel", UriKind.Relative), new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(result.GetProperty("version").GetString(), (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("version").GetString());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => JourneyDatabaseOperation.RunAsync(
            () => migrator.MigrateAsync("20261004171835_CapacityPolicyAuditEvidence")));
        Assert.Equal("P0001", refusal.SqlState);
        Assert.Equal("auditing_exports_history_retained", refusal.MessageText);
        using var retained = await client.GetAsync(Relative($"/api/auditing/exports/{id}"));
        Assert.Equal("Cancelled", (await retained.Content.ReadApiDataAsync()).GetProperty("state").GetString());
    }

    [PostgresFact]
    public async Task Operations_export_filters_final_evidence_before_time_window_and_keeps_missing_stages_unknown()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var platform = new AuditExportApp(database.ConnectionString, certificates);
        using var client = platform.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-export-password");
        var now = DateTimeOffset.UtcNow;
        var finishedOnly = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "finished",
            Outcome = "completed",
            OccurredAt = now.AddMinutes(-5),
            TraceId = "export-operation",
            HttpMethod = "POST",
            RouteTemplate = "/api/pricing/recalculate",
            StatusCode = 200,
            DurationMs = 10,
            Metadata = new OperationDetails { Action = "pricing.calculate", ExecutionRole = "endpoint" },
        };
        var startedOnly = finishedOnly with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Phase = "started", Outcome = null, StatusCode = null, DurationMs = null };
        var outsideWindow = startedOnly with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
            var ingestion = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(OperationObservedV1.Name);
            foreach (var observation in new[] { finishedOnly, startedOnly, outsideWindow,
                outsideWindow with { EventId = Guid.NewGuid(), Phase = "finished", Outcome = "completed", OccurredAt = now.AddMinutes(5), StatusCode = 200, DurationMs = 10 } })
            {
                Assert.True(await ingestion.HandleAsync(new EventEnvelope
                {
                    MessageId = observation.EventId,
                    EventName = observation.EventName,
                    OccurredAt = observation.OccurredAt,
                    Payload = serializer.Serialize(observation),
                }));
            }
        }
        using var accepted = await client.PostAsJsonAsync(new Uri("/api/auditing/exports", UriKind.Relative),
            new { requestId = Guid.NewGuid(), operations = new { source = "pricing", action = "pricing.calculate", from = now.AddHours(-1), to = now } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var status = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("operations", status.GetProperty("kind").GetString());
        Assert.Equal(2, status.GetProperty("rowCount").GetInt32());
    }

    [PostgresFact]
    public async Task Owner_accepts_one_frozen_investigation_and_replay_keeps_its_original_window_after_new_facts_arrive()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        await using var platform = new AuditExportApp(database.ConnectionString, certificates);
        using var client = platform.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-export-password");
        var fact = new AuditFact(Guid.NewGuid(), "pricing.quote-committed.v1", "pricing", "pricing.quote.changed",
            "PriceQuote", "9223372036854775807", 9, "42", DateTimeOffset.UtcNow, "export-trace", "export-correlation");
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact)).IsSuccess);
        }
        var request = new { requestId = Guid.NewGuid(), facts = new { source = "pricing", traceId = "export-trace" } };
        using var accepted = await client.PostAsJsonAsync(new Uri("/api/auditing/exports", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(1, original.GetProperty("rowCount").GetInt32());
        await using (var scope = platform.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact with { MessageId = Guid.NewGuid() })).IsSuccess);
        }
        using var replay = await client.PostAsJsonAsync(new Uri("/api/auditing/exports", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var repeated = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(original.GetProperty("exportId").GetGuid(), repeated.GetProperty("exportId").GetGuid());
        Assert.Equal(original.GetProperty("frozenAt").GetString(), repeated.GetProperty("frozenAt").GetString());
        Assert.Equal(1, repeated.GetProperty("rowCount").GetInt32());
        using var conflict = await client.PostAsJsonAsync(new Uri("/api/auditing/exports", UriKind.Relative),
            new { request.requestId, facts = new { source = "costing" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }
}

internal sealed class AuditExportApp(string connectionString, GeneratedFileCertificates certificates) : PersistentIdentityApp(connectionString, "audit-export-password")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Testing");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var files = certificates.PricingClientOptions(new Uri("https://127.0.0.1:1"));
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auditing:Exports:Enabled"] = "true",
                ["Auditing:Exports:Worker:Enabled"] = "false",
                ["Auditing:Exports:Files:BaseAddress"] = files.BaseAddress,
                ["Auditing:Exports:Files:ClientCertificatePath"] = files.ClientCertificatePath,
                ["Auditing:Exports:Files:ClientKeyPath"] = files.ClientKeyPath,
                ["Auditing:Exports:Files:RootCertificatePaths:0"] = files.RootCertificatePaths[0],
                ["Auditing:Exports:Files:RevocationMode"] = "NoCheck",
            }));
        return base.CreateHost(builder);
    }
}
