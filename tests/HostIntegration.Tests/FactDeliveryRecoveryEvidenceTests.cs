using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactDeliveryRecoveryEvidenceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public Task SchedulingPostgres_AcceptedRecoveryCannotBeRewritten_AndExpiryReleasesOnlyItsCapacity()
        => VerifyImmutableRecoveryAsync("scheduling");

    [PostgresFact]
    public Task FilesPostgres_AcceptedRecoveryCannotBeRewritten_AndExpiryReleasesOnlyItsCapacity()
        => VerifyImmutableRecoveryAsync("files");

    [PostgresFact]
    public Task PlatformPostgres_AcceptedRecoveryCannotBeRewritten_AndExpiryReleasesOnlyItsCapacity()
        => VerifyImmutableRecoveryAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_AcceptedRecoveryCannotBeRewritten_AndExpiryReleasesOnlyItsCapacity()
        => VerifyImmutableRecoveryAsync("identity");

    private async Task VerifyImmutableRecoveryAsync(string source)
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "evidence-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "evidence-root-password");
        if (source == "platform")
        {
            using var saved = await client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.evidence", UriKind.Relative),
                new { value = "private-original-content" });
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }
        else if (source == "scheduling")
        {
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = "recovery-evidence-plan",
                intervalSeconds = 30,
                firstRunInSeconds = 3600,
                targetKind = "costing.recalculate",
                targetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        else if (source == "files")
        {
            using var bytes = new ByteArrayContent([8, 4, 2]);
            using var uploaded = await client.PostAsync(new Uri("/api/files?name=recovery-evidence.bin", UriKind.Relative), bytes);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }
        else
        {
            using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                new { userName = "recovery-evidence-user", password = "evidence-user-password" });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source);
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.NotEmpty(originals);
        var original = originals[^1];
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
        DbContext migrations = source switch
        {
            "platform" => scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
            "identity" => scope.ServiceProvider.GetRequiredService<IdentityDbContext>(),
            "files" => scope.ServiceProvider.GetRequiredService<FilesDbContext>(),
            "scheduling" => scope.ServiceProvider.GetRequiredService<SchedulingDbContext>(),
            _ => throw new ArgumentException("Unknown recovery source.", nameof(source)),
        };
        var migrator = migrations.GetService<IMigrator>();
        var previous = source switch
        {
            "platform" => "20261004141238_AuditedFactCapacityPolicy",
            "identity" => "20261004152243_AuditedFactCapacityPolicy",
            "files" => "20261004155433_AuditedFactCapacityPolicy",
            "scheduling" => "20261004162527_AuditedFactCapacityPolicy",
            _ => throw new ArgumentException("Unknown recovery source.", nameof(source)),
        };
        var singlePath = new Uri($"/api/{source}/audit-deliveries/{original.Id}", UriKind.Relative);
        var stopped = (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data").Clone();
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.False(migrations.Database.HasPendingModelChanges());
        Assert.True(JsonElement.DeepEquals(stopped, (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data")));
        var request = new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" };
        var recoveryPath = new Uri($"/api/{source}/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        using var accepted = await client.PostAsJsonAsync(recoveryPath, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        var state = (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data").Clone();
        var capacityPath = new Uri($"/api/{source}/audit-deliveries/recovery-capacity", UriKind.Relative);
        var capacity = (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data").Clone();
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(source);
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source);
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforePolicies = (await policies.ReadPolicyAsync()).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var assignment in new[]
        {
            "\"RecordJson\" = '{}'", "\"RetainUntil\" = \"RetainUntil\" - interval '1 day'",
            "\"PayloadBytes\" = \"PayloadBytes\" + 1", "\"RequestId\" = @replacement",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {source}.fact_recovery_receipts SET {assignment} WHERE \"RequestId\" = @id";
            command.Parameters.AddWithValue("id", request.requestId);
            command.Parameters.AddWithValue("replacement", Guid.NewGuid());
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            Assert.Equal(source + "_fact_recovery_receipt_immutable", rejected.ConstraintName);
        }
        await using (var noOp = connection.CreateCommand())
        {
            noOp.CommandText = $"UPDATE {source}.fact_recovery_receipts SET \"RecordJson\" = \"RecordJson\" WHERE \"RequestId\" = @id";
            noOp.Parameters.AddWithValue("id", request.requestId);
            Assert.Equal(1, await noOp.ExecuteNonQueryAsync());
        }
        using var replay = await client.PostAsJsonAsync(recoveryPath, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        var receiptPath = new Uri($"/api/{source}/audit-deliveries/recoveries/{request.requestId}", UriKind.Relative);
        Assert.True(JsonElement.DeepEquals(receipt, (await client.GetFromJsonAsync<JsonElement>(receiptPath)).GetProperty("data")));
        Assert.True(JsonElement.DeepEquals(state, (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data")));
        Assert.True(JsonElement.DeepEquals(capacity, (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data")));
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previous));
        Assert.Equal("P0001", rollback.SqlState);
        Assert.Equal(source + "_fact_recovery_history_exists", rollback.ConstraintName);
        Assert.True(JsonElement.DeepEquals(receipt, (await client.GetFromJsonAsync<JsonElement>(receiptPath)).GetProperty("data")));
        IFactDeliveryRecoveryCleanup cleanup = source switch
        {
            "platform" => scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>(),
            "identity" => scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>(),
            "files" => scope.ServiceProvider.GetRequiredService<IFileAuditDelivery>(),
            "scheduling" => scope.ServiceProvider.GetRequiredService<ISchedulingAuditDelivery>(),
            _ => throw new ArgumentException("Unknown recovery source.", nameof(source)),
        };
        Assert.Equal(1, await cleanup.CleanupRecoveriesAsync(1, receipt.GetProperty("retainUntil").GetDateTimeOffset().AddTicks(10)));
        using var missing = await client.GetAsync(receiptPath);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var released = (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data").GetProperty("capacity");
        Assert.Equal("0", released.GetProperty("retainedRecords").GetString());
        Assert.Equal("0", released.GetProperty("retainedPayloadBytes").GetString());
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync();
        Assert.True(JsonElement.DeepEquals(state, (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data")));
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
    }
}
