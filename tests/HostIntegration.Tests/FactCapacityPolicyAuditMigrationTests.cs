using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyAuditMigrationTests
{
    [PostgresFact]
    public async Task CentralPolicyUpgrade_PreservesBaselineFingerprintsAndFacts_AndRefusesDestructiveDown()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var facts = LegacyFacts();
        // These four signatures were produced by the exact fingerprint projection from baseline 7c8334a.
        // They cover legacy, execution, and related-subject shapes (with and without execution).
        string[] hashes =
        [
            "F332A36E271E829AB0DAE5EF98C06C7A064AA0F152593152E32F2843542AE004",
            "D6F565D6B889ABB0BB84454C2D30D86A8237DD3A667987EAF22FA996E0FB46F1",
            "B7DC3FDA94E8242A63F767EE08B895959807042BAEA793BB42E684510AE05F31",
            "C66C060550929B3378C61212B25A3EF1CF099387513ED8C4775BA988933DBD9D"
        ];
        await using (var original = new PersistentIdentityApp(database.ConnectionString, "audit-policy-root", schedulingWorkerEnabled: false))
        {
            using var client = original.CreateClient();
            await using var scope = original.Services.CreateAsyncScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            for (var index = 0; index < facts.Length; index++)
            {
                Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(facts[index])).Value);
                // Arrange a genuinely pre-typed receipt; assertions cross ingestion and HTTP, not this shadow column.
                await using var receipt = new NpgsqlCommand("""
                    UPDATE auditing.inbox SET "AuditPayloadHash" = @hash
                    WHERE "ConsumerName" = @consumer AND "EventName" = @event AND "MessageId" = @id
                    """, connection);
                receipt.Parameters.AddWithValue("hash", hashes[index]);
                receipt.Parameters.AddWithValue("consumer", AuditIngestion.ConsumerName);
                receipt.Parameters.AddWithValue("event", facts[index].EventName);
                receipt.Parameters.AddWithValue("id", facts[index].MessageId);
                Assert.Equal(1, await receipt.ExecuteNonQueryAsync());
            }
        }
        await using var migrations = new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, AuditingDbContext.SchemaName).Options);
        var migrator = migrations.GetService<IMigrator>();
        await migrator.MigrateAsync("20261003072449_OutboxRetryRevision");
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await using var upgraded = new PersistentIdentityApp(database.ConnectionString, "audit-policy-root", schedulingWorkerEnabled: false);
        using var queryClient = upgraded.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(queryClient, "journey-root", "audit-policy-root");
        await using var upgradedScope = upgraded.Services.CreateAsyncScope();
        var restored = upgradedScope.ServiceProvider.GetRequiredService<AuditIngestion>();
        foreach (var fact in facts) { Assert.Equal(IngestionOutcome.Duplicate, (await restored.IngestAsync(fact)).Value); }
        using var legacyQuery = await queryClient.GetAsync(new Uri("/api/auditing/entries?source=platform&subjectType=global-setting&subjectId=legacy-policy-compatible", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, legacyQuery.StatusCode);
        var legacyRows = (await legacyQuery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(4, legacyRows.Length);
        Assert.Equal(facts.Select(fact => fact.MessageId).Order(), legacyRows.Select(row => row.GetProperty("fact").GetProperty("messageId").GetGuid()).Order());
        Assert.All(legacyRows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("fact").GetProperty("capacityPolicyChange").ValueKind));
        var policy = new AuditFact(Guid.NewGuid(), SettingFactCapacityPolicyChangedV1.Name, "platform", "platform.fact-capacity-policy.changed",
            "fact-capacity-policy", "platform", 2, "legacy-actor", DateTimeOffset.UtcNow, "policy-trace", "policy-correlation")
        {
            CapacityPolicyChange = new(Guid.NewGuid(), 2, "operator-adjustment")
            { Previous = new(1, 16384, 16384), Current = new(2, 32768, 16384) }
        };
        Assert.Equal(IngestionOutcome.Accepted, (await restored.IngestAsync(policy)).Value);
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261003072449_OutboxRetryRevision"));
        Assert.Equal("P0001", refusal.SqlState);
        Assert.Equal("auditing_fact_policy_history_exists", refusal.ConstraintName);
        Assert.Contains("20261004171835_CapacityPolicyAuditEvidence", await migrations.Database.GetAppliedMigrationsAsync());
        Assert.Equal(IngestionOutcome.Duplicate, (await restored.IngestAsync(policy)).Value);
        using var policyQuery = await queryClient.GetAsync(new Uri("/api/auditing/entries?source=platform&action=platform.fact-capacity-policy.changed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policyQuery.StatusCode);
        var saved = Assert.Single((await policyQuery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()).GetProperty("fact");
        Assert.Equal(policy.MessageId, saved.GetProperty("messageId").GetGuid());
        Assert.Equal(32768, saved.GetProperty("capacityPolicyChange").GetProperty("current").GetProperty("maxPayloadBytes").ReadHttpInt64());
    }

    private static AuditFact[] LegacyFacts()
    {
        var execution = new AuditExecution(Guid.Parse("00000000-0000-0000-0000-000000000010"), "platform-host",
            Guid.Parse("00000000-0000-0000-0000-000000000020"), "gateway", "legacy-initiator");
        var related = new AuditSubjectReference("identity", "user", "42");
        return Enumerable.Range(1, 4).Select(index => new AuditFact(
            Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), "platform.setting-committed.v1", "platform", "platform.setting.changed",
            "global-setting", "legacy-policy-compatible", 2, "legacy-actor", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "legacy-trace", "legacy-correlation")
        {
            Execution = index is 2 or 4 ? execution : null,
            RelatedSubject = index >= 3 ? related : null
        }).ToArray();
    }
}
