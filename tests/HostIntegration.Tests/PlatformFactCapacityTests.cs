using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class PlatformFactCapacityTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task FailedInsert_RollsBackItsQuota_AndCommittedEnvelopeCannotBeRewritten()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options;
        await using var context = new PlatformDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE platform.fact_capacity SET "MaxRecords" = 1;
            CREATE FUNCTION platform.reject_after_accounting() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test failure after capacity reservation'; END $$;
            CREATE TRIGGER zz_reject_after_accounting AFTER INSERT ON platform.outbox
                FOR EACH ROW EXECUTE FUNCTION platform.reject_after_accounting();
            """);
        var now = DateTimeOffset.UtcNow;
        var first = new OutboxEntry { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "{}", OccurredAt = now };
        context.Outbox.Add(first);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        var store = new EfOutboxStore<PlatformDbContext>(context);
        Assert.Empty(await store.ReadPendingAsync(10, now));
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER zz_reject_after_accounting ON platform.outbox");
        context.Outbox.Add(first);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Assert.Equal(first.Id, Assert.Single(await store.ReadPendingAsync(10, now)).Id);
        Assert.Equal(0, await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.outbox ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount")
            VALUES ({first.Id}, {first.EventName}, {first.Payload}, {first.OccurredAt}, 0) ON CONFLICT ("Id") DO NOTHING
            """));
        var replacement = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.outbox SET \"Id\" = {replacement} WHERE \"Id\" = {first.Id}"));
        Assert.Equal("platform_fact_immutable", error.ConstraintName);
        Assert.Equal(first.Id, Assert.Single(await store.ReadPendingAsync(10, now)).Id);
    }

    [PostgresFact]
    public async Task PayloadQuota_UsesUtf8Bytes_AndFailedCleanupDoesNotReleaseCapacity()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options;
        await using var context = new PlatformDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE platform.fact_capacity SET "MaxRecords" = 10, "MaxPayloadBytes" = 6, "MaxRecordPayloadBytes" = 3;
            """);
        var now = DateTimeOffset.UtcNow;
        var first = new OutboxEntry { Id = Guid.NewGuid(), EventName = SettingCommittedV1.Name, Payload = "中", OccurredAt = now };
        var second = first with { Id = Guid.NewGuid(), Payload = "文" };
        context.Outbox.AddRange(first, second);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var business = first with { Id = Guid.NewGuid(), EventName = "platform.business.v1", Payload = new string('x', 100) };
        context.Outbox.Add(business);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await AssertPayloadRejectedAsync(context, first with { Id = Guid.NewGuid(), Payload = "a" });
        var store = new EfOutboxStore<PlatformDbContext>(context);
        await store.MarkDeliveredAsync(first.Id, now);
        context.ChangeTracker.Clear();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION platform.reject_capacity_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test maintenance fault'; END $$;
            CREATE TRIGGER zz_reject_capacity_cleanup AFTER DELETE ON platform.outbox
                FOR EACH ROW EXECUTE FUNCTION platform.reject_capacity_cleanup();
            """);
        var cleanup = new EfCommittedFactCleanup<PlatformDbContext>(context, SettingCommittedV1.Name, new(), new FixedClock(now.AddDays(8)));
        await Assert.ThrowsAsync<PostgresException>(() => cleanup.CleanupAsync());
        await AssertPayloadRejectedAsync(context, first with { Id = Guid.NewGuid(), Payload = "a" });
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER zz_reject_capacity_cleanup ON platform.outbox");
        Assert.Equal(1, await cleanup.CleanupAsync());
        // Isolate the single-record limit from the total-byte limit.
        await context.Database.ExecuteSqlRawAsync("UPDATE platform.fact_capacity SET \"MaxPayloadBytes\" = 100");
        await AssertPayloadRejectedAsync(context, first with { Id = Guid.NewGuid(), Payload = "中文" });
        await context.Database.ExecuteSqlRawAsync("UPDATE platform.fact_capacity SET \"MaxPayloadBytes\" = 6");
        var replacement = first with { Id = Guid.NewGuid(), Payload = "ab" };
        context.Outbox.Add(replacement);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await AssertPayloadRejectedAsync(context, first with { Id = Guid.NewGuid(), Payload = "ab" });
        Assert.Equal(new[] { second.Id, replacement.Id, business.Id }.Order(),
            (await store.ReadPendingAsync(10, now)).Select(entry => entry.Id).Order());
    }

    private static async Task AssertPayloadRejectedAsync(PlatformDbContext context, OutboxEntry entry)
    {
        context.Outbox.Add(entry);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal("platform_fact_capacity_exhausted", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        context.ChangeTracker.Clear();
    }

    [PostgresFact]
    public async Task ConcurrentWrites_RespectRemainingFactCapacity_AndCleanupAllowsBusinessToResume()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "capacity-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-root-password");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // Test-only policy: expose the last available slot without creating 100,000 business records.
        await using (var policy = new NpgsqlCommand("UPDATE platform.fact_capacity SET \"MaxRecords\" = 1", connection))
        {
            Assert.Equal(1, await policy.ExecuteNonQueryAsync());
        }
        var writes = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => client.PutAsJsonAsync(
            new Uri($"/api/platform/settings/capacity.item{index}", UriKind.Relative), new { value = "original", expectedVersion = 0 })));
        try
        {
            Assert.Single(writes, response => response.StatusCode == HttpStatusCode.NoContent);
            foreach (var response in writes.Where(response => response.StatusCode != HttpStatusCode.NoContent))
            {
                await AssertCapacityFailureAsync(response);
            }
        }
        finally { foreach (var response in writes) { response.Dispose(); } }
        using var list = await client.GetAsync(new Uri("/api/platform/settings/?scope=capacity", UriKind.Relative));
        var setting = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        var uri = new Uri("/api/platform/settings/" + setting.GetProperty("key").GetString(), UriKind.Relative);
        using var noOp = await client.PutAsJsonAsync(uri, new { value = "original", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode);
        using var change = await client.PutAsJsonAsync(uri, new { value = "must-rollback", expectedVersion = 1 });
        await AssertCapacityFailureAsync(change);
        using var unchanged = await client.GetAsync(uri);
        var original = await unchanged.Content.ReadApiDataAsync();
        Assert.Equal("original", original.GetProperty("value").GetString());
        Assert.Equal(1, original.GetProperty("version").ReadHttpInt64());

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
            var fact = Assert.Single(await delivery.ListAsync("Pending", 10));
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
            await outbox.MarkDeliveredAsync(fact.MessageId, DateTimeOffset.UtcNow);
            var cleanup = new EfCommittedFactCleanup<PlatformDbContext>(scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
                SettingCommittedV1.Name, new(), new FixedClock(DateTimeOffset.UtcNow.AddDays(8)));
            Assert.Equal(1, await cleanup.CleanupAsync());
        }
        using var recovered = await client.PutAsJsonAsync(uri, new { value = "recovered", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        using var read = await client.GetAsync(uri);
        var final = await read.Content.ReadApiDataAsync();
        Assert.Equal("recovered", final.GetProperty("value").GetString());
        Assert.Equal(2, final.GetProperty("version").ReadHttpInt64());
    }

    private static async Task AssertCapacityFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("platform.audit_capacity.exhausted", error.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("fact_capacity", error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("must-rollback", error.GetRawText(), StringComparison.Ordinal);
    }
}
