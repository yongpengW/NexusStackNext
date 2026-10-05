using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;
using RecoveryMigration = NexusStackNext.Costing.Infrastructure.Migrations.ConditionalFactRecovery;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CostingFactRecoveryEvidenceTests
{
    [PostgresFact]
    public async Task NormalMigration_PreservesExistingCostTaskAndPolicy_AndRecoveryEvidenceSurvivesProcessRestartAndRejectsRewrite()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        var assembly = typeof(CostingHostMarker).Assembly.Location;
        var settings = new Dictionary<string, string> { ["Costing__AuditDelivery__RecoveryMaintenance__Enabled"] = "false" };
        await using var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, null);
        await using var scope = app.Services.CreateAsyncScope();
        // Public migration metadata identifies the owned context without exposing its internal type for tests.
        Migration migration = new RecoveryMigration();
        var contextType = migration.GetType().GetCustomAttribute<DbContextAttribute>()?.ContextType;
        Assert.NotNull(contextType);
        var context = Assert.IsAssignableFrom<DbContext>(scope.ServiceProvider.GetRequiredService(contextType));
        var migrator = context.GetService<IMigrator>();
        const string previousMigration = "20261004164558_AuditedFactCapacityPolicy";
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var costPath = new Uri($"/api/costing/items/{itemId}", UriKind.Relative);
        var taskPath = new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative);
        var policyPath = new Uri("/api/costing/audit-capacity", UriKind.Relative);
        JsonElement cost;
        JsonElement task;
        JsonElement policy;
        await using (var original = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString, settings: settings))
        {
            original.Authenticate();
            using var created = await original.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative),
                new { requestId = taskId, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m });
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            using var adjusted = await original.Client.PutAsJsonAsync(policyPath,
                new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 100001, 256 * 1024 * 1024, 16 * 1024, "operator-adjustment"));
            Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
            cost = await ReadAsync(original.Client, costPath);
            task = await ReadAsync(original.Client, taskPath);
            policy = await ReadAsync(original.Client, policyPath);
        }
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var target = Assert.Single(originals, entry => entry.EventName == CostingFactCapacityPolicyChangedV1.Name);
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var delivery = scope.ServiceProvider.GetRequiredService<ICostingAuditDelivery>();
        var stopped = (await delivery.GetAsync(target.Id)).Value;
        await migrator.MigrateAsync(previousMigration);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(stopped, (await delivery.GetAsync(target.Id)).Value);
        var request = new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" };
        var retryPath = new Uri($"/api/costing/audit-deliveries/{target.Id}/retry", UriKind.Relative);
        var receiptPath = new Uri($"/api/costing/audit-deliveries/recoveries/{request.requestId}", UriKind.Relative);
        JsonElement receipt;
        await using (var upgraded = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString, settings: settings))
        {
            upgraded.Authenticate();
            Assert.True(JsonElement.DeepEquals(cost, await ReadAsync(upgraded.Client, costPath)));
            Assert.True(JsonElement.DeepEquals(task, await ReadAsync(upgraded.Client, taskPath)));
            Assert.True(JsonElement.DeepEquals(policy, await ReadAsync(upgraded.Client, policyPath)));
            using var accepted = await upgraded.Client.PostAsJsonAsync(retryPath, request);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            receipt = (await accepted.Content.ReadApiDataAsync()).Clone();
        }
        var recovered = (await delivery.GetAsync(target.Id)).Value;
        var capacity = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, pending.Id);
        Assert.Equal(target.Payload, pending.Payload);
        Assert.Equal(target.OccurredAt, pending.OccurredAt);
        await using (var restarted = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString, settings: settings))
        {
            restarted.Authenticate();
            Assert.True(JsonElement.DeepEquals(receipt, await ReadAsync(restarted.Client, receiptPath)));
            using var replay = await restarted.Client.PostAsJsonAsync(retryPath, request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
            Assert.True(JsonElement.DeepEquals(cost, await ReadAsync(restarted.Client, costPath)));
            Assert.True(JsonElement.DeepEquals(task, await ReadAsync(restarted.Client, taskPath)));
            Assert.True(JsonElement.DeepEquals(policy, await ReadAsync(restarted.Client, policyPath)));
        }
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var assignment in new[]
        {
            "\"RecordJson\" = '{}'", "\"PayloadBytes\" = \"PayloadBytes\" + 1",
            "\"RetainUntil\" = \"RetainUntil\" - interval '1 day'", "\"RequestId\" = @replacement",
        })
        {
            await using var rewrite = new NpgsqlCommand($"UPDATE costing.fact_recovery_receipts SET {assignment} WHERE \"RequestId\" = @id", connection);
            rewrite.Parameters.AddWithValue("id", request.requestId);
            rewrite.Parameters.AddWithValue("replacement", Guid.NewGuid());
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => rewrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            Assert.Equal("costing_fact_recovery_receipt_immutable", rejected.ConstraintName);
        }
        await using (var unchanged = new NpgsqlCommand("UPDATE costing.fact_recovery_receipts SET \"RecordJson\" = \"RecordJson\" WHERE \"RequestId\" = @id", connection))
        {
            unchanged.Parameters.AddWithValue("id", request.requestId);
            Assert.Equal(1, await unchanged.ExecuteNonQueryAsync());
        }
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, rollback.SqlState);
        Assert.Equal("costing_fact_recovery_history_exists", rollback.ConstraintName);
        var retained = (await delivery.GetRecoveryAsync(request.requestId)).Value;
        Assert.Equal(request.requestId, retained.RequestId);
        Assert.Equal(capacity, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(recovered, (await delivery.GetAsync(target.Id)).Value);
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, retained.RetainUntil.AddTicks(10)));
        Assert.Equal("costing.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.requestId)).Error.Code);
        var released = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
        Assert.Equal(0, released.RetainedRecords);
        Assert.Equal(0, released.RetainedPayloadBytes);
        await migrator.MigrateAsync(previousMigration);
        await migrator.MigrateAsync();
        Assert.Equal(recovered, (await delivery.GetAsync(target.Id)).Value);
        Assert.Equal(pending, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Uri path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadApiDataAsync()).Clone();
    }
}
