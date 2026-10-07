using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditPersistenceJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task UnmigratedAuditDatabase_RefusesStartup_UntilIndependentMigration()
    {
        await using var business = await databases.CreateAsync();
        await using var auditing = await IdentityJourneyDatabase.CreateAsync();
        await using (var unprepared = new PersistentIdentityApp(business.ConnectionString, auditingConnectionString: auditing.ConnectionString))
        {
            var error = Assert.ThrowsAny<Exception>(() => unprepared.CreateClient());
            Assert.Contains("Auditing 数据库需要迁移", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(auditing.ConnectionString, "Auditing")).ExitCode);
        await using var prepared = new PersistentIdentityApp(business.ConnectionString, auditingConnectionString: auditing.ConnectionString);
        using var client = prepared.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [PostgresFact]
    public async Task FailedRecordDoesNotConsumeIdentity_AndConcurrentRedeliveryCreatesOneRecord()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "audit-root-password");
        using var client = app.CreateClient();
        var fact = new AuditFact(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
            "global-setting", "audit.atomic", 2, "42", DateTimeOffset.UtcNow, "trace-atomic", "correlation-atomic");
        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var reject = new NpgsqlCommand("ALTER TABLE auditing.audit_entries ADD CONSTRAINT reject_test_record CHECK (\"SubjectId\" <> 'audit.atomic')", fault))
        {
            await reject.ExecuteNonQueryAsync();
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact));
        }
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-root-password");
        using var absent = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
        Assert.Empty((await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        await using (var release = new NpgsqlCommand("ALTER TABLE auditing.audit_entries DROP CONSTRAINT reject_test_record", fault))
        {
            await release.ExecuteNonQueryAsync();
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact);
        }));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Value == IngestionOutcome.Accepted);
        Assert.Equal(3, results.Count(result => result.Value == IngestionOutcome.Duplicate));
        using var saved = await client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var entry = Assert.Single((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.Equal(fact.MessageId, entry.GetProperty("fact").GetProperty("messageId").GetGuid());
    }

    [PostgresFact]
    public async Task IndependentMigration_StoresFactsAndIdentityAcrossHostRestart()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateThroughCliAsync();
        var migration = await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing");
        Assert.Equal(0, migration.ExitCode);
        Assert.Contains("Auditing migrations applied.", migration.Output, StringComparison.Ordinal);
        var fact = new AuditFact(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
            "global-setting", "1001", 2, "42", DateTimeOffset.Parse("2026-10-02T01:02:03.123456Z", System.Globalization.CultureInfo.InvariantCulture), "trace-1", "correlation-1")
        { Execution = new AuditExecution(Guid.NewGuid(), "platform-host", Guid.NewGuid(), "gateway", "original-user") };
        await using (var first = new PersistentIdentityApp(database.ConnectionString, "audit-root-password"))
        {
            using var client = first.CreateClient();
            await using var scope = first.Services.CreateAsyncScope();
            Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(fact)).Value);
        }
        await using var restarted = new PersistentIdentityApp(database.ConnectionString, "audit-root-password");
        using var reader = restarted.CreateClient();
        await using (var scope = restarted.Services.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
            Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(fact)).Value);
            Assert.Equal("auditing.message_conflict", (await ingestion.IngestAsync(fact with { ActorId = "forged" })).Error.Code);
            foreach (var replacement in new AuditExecution?[]
            {
                null,
                fact.Execution with { OperationId = Guid.NewGuid() },
                fact.Execution with { Source = "another-host" },
                fact.Execution with { RootOperationId = Guid.NewGuid() },
                fact.Execution with { RootSource = "another-root" },
                fact.Execution with { InitiatorId = "another-user" },
            })
            {
                Assert.Equal("auditing.message_conflict", (await ingestion.IngestAsync(fact with { Execution = replacement })).Error.Code);
            }
        }
        await PlatformSettingsAccessTests.LoginAsync(reader, "journey-root", "audit-root-password");
        using var response = await reader.GetAsync(new Uri("/api/auditing/entries?from=2026-10-02T00:00:00Z&to=2026-10-03T00:00:00Z", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var saved = Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact");
        Assert.Equal(fact.MessageId, saved.GetProperty("messageId").GetGuid());
        Assert.Equal("42", saved.GetProperty("actorId").GetString());
        Assert.Equal(fact.OccurredAt, saved.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(fact.Execution.OperationId, saved.GetProperty("execution").GetProperty("operationId").GetGuid());
    }
}
