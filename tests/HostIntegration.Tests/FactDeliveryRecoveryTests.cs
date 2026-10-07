using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactDeliveryRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PlatformPostgres_PolicyFactIsVisibleInBoundedHttpInvestigation_AndCanRecoverWithTheSameMessageIdentity()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-recovery-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-recovery-password");
        var policyPath = new Uri("/api/platform/audit-capacity", UriKind.Relative);
        var policy = (await client.GetFromJsonAsync<JsonElement>(policyPath)).GetProperty("data");
        using var adjusted = await client.PutAsJsonAsync(policyPath, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = policy.GetProperty("policyRevision").GetString(),
            maxRecords = (policy.GetProperty("maxRecords").ReadHttpInt64() + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            maxPayloadBytes = policy.GetProperty("maxPayloadBytes").GetString(),
            maxRecordPayloadBytes = policy.GetProperty("maxRecordPayloadBytes").GetInt32(),
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal("platform.fact-capacity-policy-changed.v1", original.EventName);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(DateTimeOffset.UtcNow), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = (await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=DeadLettered&limit=1", UriKind.Relative))).GetProperty("data");
        var observed = Assert.Single(stopped.EnumerateArray());
        Assert.Equal(original.Id, observed.GetProperty("messageId").GetGuid());
        Assert.Equal("0", observed.GetProperty("retryRevision").GetString());
        Assert.False(observed.TryGetProperty("payload", out _));
        Assert.False(observed.TryGetProperty("lastFailure", out _));
        using var recovered = await client.PostAsJsonAsync(
            new Uri($"/api/platform/audit-deliveries/{original.Id}/retry", UriKind.Relative), new
            {
                requestId = Guid.NewGuid(),
                expectedDeadLetteredAt = observed.GetProperty("deadLetteredAt").GetDateTimeOffset(),
                expectedRetryRevision = observed.GetProperty("retryRevision").GetString(),
                reason = "dependency-restored",
            });
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var pending = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.EventName, pending.EventName);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        var visible = (await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=Pending&limit=1", UriKind.Relative))).GetProperty("data");
        Assert.Equal(original.Id, Assert.Single(visible.EnumerateArray()).GetProperty("messageId").GetGuid());
    }

    [PostgresFact]
    public async Task PlatformPostgres_CleanupRoundsRetentionUp_AndReleasesOnlyOneStableBatch()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var businessCapacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("cleanup.first").Value, "retained-first")).IsSuccess);
        Assert.True((await settings.WriteAsync(SettingKey.Create("cleanup.second").Value, "retained-second")).IsSuccess);
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = await delivery.ListAsync("DeadLettered", 10);
        var observed = stopped[0].DeadLetteredAt!.Value;
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Assert.True((await delivery.RecoverAsync(new(secondId, originals[1].Id, observed, 0, "manual-retry"), "cleanup-operator", now, null)).IsSuccess);
        var first = await delivery.RecoverAsync(new(firstId, originals[0].Id, observed, 0, "manual-retry"), "cleanup-operator", now, null);
        Assert.True(first.IsSuccess);
        Assert.Equal(now.AddDays(7), first.Value.RetainUntil);
        var beforeFacts = (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray();
        var beforeBusiness = (await businessCapacity.ReadAsync()).Value;
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(2, beforeRecovery.Capacity.RetainedRecords);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, first.Value.RetainUntil.AddTicks(-1)));
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, first.Value.RetainUntil));
        Assert.True((await delivery.GetRecoveryAsync(firstId)).IsSuccess);
        Assert.True((await delivery.GetRecoveryAsync(secondId)).IsSuccess);
        // The first representable microsecond after the advertised fractional deadline is safe to clean.
        var safeDeadline = first.Value.RetainUntil.AddTicks(9);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, safeDeadline));
        Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(firstId)).Error.Code);
        Assert.True((await delivery.GetRecoveryAsync(secondId)).IsSuccess);
        var afterBatch = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, afterBatch.Capacity.RetainedRecords);
        Assert.InRange(afterBatch.Capacity.RetainedPayloadBytes, 1, beforeRecovery.Capacity.RetainedPayloadBytes - 1);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, safeDeadline));
        var released = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(0, released.Capacity.RetainedRecords);
        Assert.Equal(0, released.Capacity.RetainedPayloadBytes);
        Assert.Equal(beforeFacts, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray());
        Assert.Equal(beforeBusiness, (await businessCapacity.ReadAsync()).Value);
    }

    [Fact]
    public async Task PlatformMemory_CleanupHonorsFixedRetentionAndStableBatch_WithoutTouchingSourceFacts()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var businessCapacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("cleanup.first").Value, "retained-first")).IsSuccess);
        Assert.True((await settings.WriteAsync(SettingKey.Create("cleanup.second").Value, "retained-second")).IsSuccess);
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        // Reverse insertion order with the same deadline; stable request order decides the first batch.
        Assert.True((await delivery.RecoverAsync(new(secondId, originals[1].Id, now, 0, "manual-retry"), "cleanup-operator", now, null)).IsSuccess);
        var first = await delivery.RecoverAsync(new(firstId, originals[0].Id, now, 0, "manual-retry"), "cleanup-operator", now, null);
        Assert.True(first.IsSuccess);
        Assert.Equal(now.AddDays(7), first.Value.RetainUntil);
        var beforeFacts = (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray();
        var beforeBusiness = (await businessCapacity.ReadAsync()).Value;
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(2, beforeRecovery.Capacity.RetainedRecords);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7).AddTicks(-1)));
        Assert.True((await delivery.GetRecoveryAsync(firstId)).IsSuccess);
        Assert.True((await delivery.GetRecoveryAsync(secondId)).IsSuccess);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
        Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(firstId)).Error.Code);
        Assert.True((await delivery.GetRecoveryAsync(secondId)).IsSuccess);
        var afterBatch = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, afterBatch.Capacity.RetainedRecords);
        Assert.InRange(afterBatch.Capacity.RetainedPayloadBytes, 1, beforeRecovery.Capacity.RetainedPayloadBytes - 1);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
        var released = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(0, released.Capacity.RetainedRecords);
        Assert.Equal(0, released.Capacity.RetainedPayloadBytes);
        Assert.Equal(beforeFacts, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray());
        Assert.Equal(beforeBusiness, (await businessCapacity.ReadAsync()).Value);
    }

    [Fact]
    public async Task PlatformMemory_RecoveryCapacityCountsOnlyNewReceipts_WithoutChargingOtherPools()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        var diagnosticPath = new Uri("/api/platform/audit-deliveries/recovery-capacity", UriKind.Relative);
        using var empty = await client.GetAsync(diagnosticPath);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var initial = (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("platform", initial.GetProperty("source").GetString());
        Assert.False(initial.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("1000", initial.GetProperty("capacity").GetProperty("maxRecords").GetString());
        Assert.Equal("0", initial.GetProperty("capacity").GetProperty("retainedRecords").GetString());
        using var saved = await client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.capacity", UriKind.Relative), new { value = "retained" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var otherPools = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/platform/audit-capacity", UriKind.Relative));
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var now = DateTimeOffset.UtcNow;
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var request = new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = now, expectedRetryRevision = "0", reason = "manual-retry" };
        var recoveryPath = new Uri($"/api/platform/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(recoveryPath,
            new { requestId = request.requestId, expectedDeadLetteredAt = now, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var accepted = await client.PostAsJsonAsync(recoveryPath, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var counted = (await client.GetFromJsonAsync<JsonElement>(diagnosticPath)).GetProperty("data");
        Assert.Equal("1", counted.GetProperty("capacity").GetProperty("retainedRecords").GetString());
        Assert.True(long.Parse(counted.GetProperty("capacity").GetProperty("retainedPayloadBytes").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture) > 0);
        using var replay = await client.PostAsJsonAsync(recoveryPath, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var unchanged = (await client.GetFromJsonAsync<JsonElement>(diagnosticPath)).GetProperty("data");
        Assert.True(JsonElement.DeepEquals(counted, unchanged));
        var after = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/platform/audit-capacity", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(otherPools.GetProperty("data"), after.GetProperty("data")));
    }

    [PostgresFact]
    public async Task PlatformPostgres_RecoveryMigrationPreservesStoppedFact_AndRefusesRollbackOfAcceptedHistory()
    {
        await using var database = await databases.CreateAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options;
        Guid messageId;
        DateTimeOffset observed;
        await using (var before = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(before.Client, "journey-root", "recovery-root-password");
            using var saved = await before.Client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.upgrade", UriKind.Relative),
                new { value = "retained" });
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            await using var source = new PlatformDbContext(options);
            var outbox = new EfOutboxStore<PlatformDbContext>(source);
            messageId = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id;
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(DateTimeOffset.UtcNow), new() { MaxAttempts = 1 });
            Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
            var stopped = await before.Client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
            observed = Assert.Single(stopped.GetProperty("data").EnumerateArray()).GetProperty("deadLetteredAt").GetDateTimeOffset();
        }
        await using var migrations = new PlatformDbContext(options);
        var migrator = migrations.GetService<IMigrator>();
        const string previous = "20261004141238_AuditedFactCapacityPolicy";
        // Empty recovery history can be removed; the original stopped fact must survive upgrade.
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.False(migrations.Database.HasPendingModelChanges());
        var requestId = Guid.Parse("4e08f7d8-77ef-49bc-9790-83fd8db66d82");
        JsonElement receipt;
        await using (var upgraded = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(upgraded.Client, "journey-root", "recovery-root-password");
            var stopped = await upgraded.Client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
            var delivery = Assert.Single(stopped.GetProperty("data").EnumerateArray());
            Assert.Equal(messageId, delivery.GetProperty("messageId").GetGuid());
            Assert.Equal(observed, delivery.GetProperty("deadLetteredAt").GetDateTimeOffset());
            Assert.Equal("0", delivery.GetProperty("retryRevision").GetString());
            using var accepted = await upgraded.Client.PostAsJsonAsync(
                new Uri($"/api/platform/audit-deliveries/{messageId}/retry", UriKind.Relative),
                new { requestId, expectedDeadLetteredAt = observed, expectedRetryRevision = "0", reason = "manual-retry" });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            receipt = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        }
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previous));
        Assert.Equal("P0001", refusal.SqlState);
        Assert.Equal("platform_fact_recovery_history_exists", refusal.ConstraintName);
        await using var retained = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password");
        await PlatformSettingsAccessTests.LoginAsync(retained.Client, "journey-root", "recovery-root-password");
        var found = await retained.Client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/platform/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(receipt, found.GetProperty("data")));
    }

    [PostgresFact]
    public async Task PlatformPostgres_SubMicrosecondChangedStopCondition_IsRejectedWithoutReservingRequest()
    {
        await using var database = await databases.CreateAsync();
        await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password");
        await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "recovery-root-password");
        using var saved = await host.Client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.precision", UriKind.Relative),
            new { value = "retained" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options;
        await using var context = new PlatformDbContext(options);
        var outbox = new EfOutboxStore<PlatformDbContext>(context);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(DateTimeOffset.UtcNow), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = await host.Client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
        var observed = Assert.Single(stopped.GetProperty("data").EnumerateArray()).GetProperty("deadLetteredAt").GetDateTimeOffset();
        var requestId = Guid.Parse("c858dd27-7ed8-4161-a98b-d6b2ba96b986");
        var path = new Uri($"/api/platform/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        using var rejected = await host.Client.PostAsJsonAsync(path,
            new { requestId, expectedDeadLetteredAt = observed.AddTicks(1), expectedRetryRevision = "0", reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var missing = await host.Client.GetAsync(new Uri($"/api/platform/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var accepted = await host.Client.PostAsJsonAsync(path,
            new { requestId, expectedDeadLetteredAt = observed, expectedRetryRevision = "0", reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("1", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("retryRevision").GetString());
    }

    [PostgresFact]
    public async Task PlatformPostgres_RecoveryReceiptSurvivesSourceProcessRestart()
    {
        await using var database = await databases.CreateAsync();
        var requestId = Guid.Parse("d7e3504e-a961-48b6-a7a5-ab018df51c80");
        Guid messageId;
        DateTimeOffset stoppedAt;
        JsonElement receipt;
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "recovery-root-password");
            using var saved = await first.Client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.persisted", UriKind.Relative),
                new { value = "retained" });
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNexusStackPostgres(database.ConnectionString, PlatformDbContext.SchemaName).Options;
            await using var context = new PlatformDbContext(options);
            var outbox = new EfOutboxStore<PlatformDbContext>(context);
            var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            messageId = original.Id;
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(DateTimeOffset.UtcNow), new() { MaxAttempts = 1 });
            Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
            var stopped = await first.Client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
            stoppedAt = Assert.Single(stopped.GetProperty("data").EnumerateArray()).GetProperty("deadLetteredAt").GetDateTimeOffset();
            using var accepted = await first.Client.PostAsJsonAsync(
                new Uri($"/api/platform/audit-deliveries/{messageId}/retry", UriKind.Relative),
                new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            receipt = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        }
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "recovery-root-password");
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "recovery-root-password");
        var found = await restarted.Client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/platform/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.True(JsonElement.DeepEquals(receipt, found.GetProperty("data")));
        var capacityPath = new Uri("/api/platform/audit-deliveries/recovery-capacity", UriKind.Relative);
        using var diagnostic = await restarted.Client.GetAsync(capacityPath);
        Assert.Equal(HttpStatusCode.OK, diagnostic.StatusCode);
        var capacity = (await diagnostic.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("platform", capacity.GetProperty("source").GetString());
        Assert.True(capacity.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("1000", capacity.GetProperty("capacity").GetProperty("maxRecords").GetString());
        Assert.Equal("1", capacity.GetProperty("capacity").GetProperty("retainedRecords").GetString());
        using var replay = await restarted.Client.PostAsJsonAsync(
            new Uri($"/api/platform/audit-deliveries/{messageId}/retry", UriKind.Relative),
            new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        var unchangedCapacity = await restarted.Client.GetFromJsonAsync<JsonElement>(capacityPath);
        Assert.True(JsonElement.DeepEquals(capacity, unchangedCapacity.GetProperty("data")));
        var pending = await restarted.Client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=Pending", UriKind.Relative));
        var delivery = Assert.Single(pending.GetProperty("data").EnumerateArray());
        Assert.Equal(messageId, delivery.GetProperty("messageId").GetGuid());
        Assert.Equal("1", delivery.GetProperty("retryRevision").GetString());
    }

    [Fact]
    public async Task PlatformMemory_LostRecoveryResponse_IsReadableByRequestId_WithoutReopeningDelivery()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        using var saved = await client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.lost-response", UriKind.Relative),
            new { value = "retained" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var now = DateTimeOffset.UtcNow;
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var requestId = Guid.Parse("bb2af45f-164a-41e7-8235-9f2097fb460e");
        using (var response = await client.PostAsJsonAsync(
            new Uri($"/api/platform/audit-deliveries/{original.Id}/retry", UriKind.Relative), new
            {
                requestId,
                expectedDeadLetteredAt = now,
                expectedRetryRevision = "0",
                reason = "manual-retry",
                actorId = "client-forged",
                source = "pricing",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Discard the response body; only the stable request identity remains with the caller.
        }
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var path = new Uri($"/api/platform/audit-deliveries/recoveries/{requestId}", UriKind.Relative);
        using var read = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var receipt = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(original.Id, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal("platform", receipt.GetProperty("source").GetString());
        Assert.NotEqual("client-forged", receipt.GetProperty("actorId").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        var again = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.True(JsonElement.DeepEquals(receipt, again.GetProperty("data")));
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task PlatformMemory_RetryReturnsStableReceipt_AndReplayDoesNotReopenLaterBudget()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        using var saved = await client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.receipt", UriKind.Relative),
            new { value = "retained" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var now = DateTimeOffset.UtcNow;
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
        var delivery = Assert.Single(stopped.GetProperty("data").EnumerateArray());
        Assert.Equal(original.Id, delivery.GetProperty("messageId").GetGuid());
        var requestId = Guid.Parse("f684cd24-0404-4df2-9b73-d20ce1a26061");
        var request = new
        {
            requestId,
            expectedDeadLetteredAt = delivery.GetProperty("deadLetteredAt").GetDateTimeOffset(),
            expectedRetryRevision = "0",
            reason = "dependency-restored",
        };
        var path = new Uri($"/api/platform/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        using var accepted = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var first = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(first.TryGetProperty("requestId", out var acceptedId), "恢复响应缺少稳定请求凭据。");
        Assert.Equal(requestId, acceptedId.GetGuid());
        Assert.Equal(original.Id, first.GetProperty("messageId").GetGuid());
        Assert.Equal("1", first.GetProperty("retryRevision").GetString());
        var pending = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(1, pending.RetryRevision);

        // A later round can stop at the same clock time. Replaying the original request must keep its first decision.
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replayed = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = (await replayed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(JsonElement.DeepEquals(first, replay));
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var later = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative));
        var stillStopped = Assert.Single(later.GetProperty("data").EnumerateArray());
        Assert.Equal(original.Id, stillStopped.GetProperty("messageId").GetGuid());
        Assert.Equal("1", stillStopped.GetProperty("retryRevision").GetString());
    }
}
