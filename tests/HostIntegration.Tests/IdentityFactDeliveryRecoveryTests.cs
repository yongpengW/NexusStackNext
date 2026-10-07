using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class IdentityFactDeliveryRecoveryTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresCleanup_UsesTheFixedDeadlineAndStableBatch_ReleasesOnlyRecoveryCapacity()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "cleanup-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await VerifyRecoveryCleanupAsync(client, app.Services, true);
    }

    [Fact]
    public async Task MemoryCleanup_UsesTheFixedDeadlineAndStableBatch_ReleasesOnlyRecoveryCapacity()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await VerifyRecoveryCleanupAsync(client, app.Services, false);
    }

    private static async Task VerifyRecoveryCleanupAsync(HttpClient client, IServiceProvider services, bool isPersistent)
    {
        foreach (var name in new[] { "recovery-cleanup-one", "recovery-cleanup-two" })
        {
            using var created = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                new { userName = name, password = "recovery-cleanup-password" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        await using var scope = services.CreateAsyncScope();
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var originals = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Take(2).ToArray();
        Assert.Equal(2, originals.Length);
        var stoppedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var acceptedAt = stoppedAt.AddTicks(1);
        foreach (var original in originals)
        { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-cleanup-stop", stoppedAt, 0)); }
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var firstRequest = new FactDeliveryRecoveryRequest(firstId, originals[0].Id, stoppedAt, 0, "manual-retry");
        Assert.True((await delivery.RecoverAsync(new(secondId, originals[1].Id, stoppedAt, 0, "manual-retry"),
            "cleanup-operator", acceptedAt, null)).IsSuccess);
        var accepted = await delivery.RecoverAsync(firstRequest, "cleanup-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(1), accepted.Value.RetainUntil);
        var beforeFacts = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).ToArray();
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforePolicies = (await policies.ReadPolicyAsync()).Value;
        var before = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(2, before.Capacity.RetainedRecords);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, stoppedAt.AddDays(7)));
        if (isPersistent)
        { Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, acceptedAt.AddDays(7))); }
        var deadline = isPersistent ? stoppedAt.AddDays(7).AddTicks(10) : acceptedAt.AddDays(7);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, deadline));
        Assert.Equal("identity.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(firstId)).Error.Code);
        Assert.True((await delivery.GetRecoveryAsync(secondId)).IsSuccess);
        var remaining = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, remaining.Capacity.RetainedRecords);
        Assert.InRange(remaining.Capacity.RetainedPayloadBytes, 1, before.Capacity.RetainedPayloadBytes - 1);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, deadline));
        var released = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(0, released.Capacity.RetainedRecords);
        Assert.Equal(0, released.Capacity.RetainedPayloadBytes);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, deadline));
        Assert.Equal(beforeFacts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal("identity.delivery_conflict",
            (await delivery.RecoverAsync(firstRequest, "cleanup-operator", deadline, null)).Error.Code);
    }

    [PostgresFact]
    public async Task PostgresHttp_RecoveryCapacityIsIndependent_AndReplayDoesNotConsumeItAgain()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "capacity-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "capacity-root-password");
        await VerifyRecoveryCapacityAsync(client, app.Services, true);
    }

    [Fact]
    public async Task MemoryHttp_RecoveryCapacityIsIndependent_AndReplayDoesNotConsumeItAgain()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyRecoveryCapacityAsync(client, app.Services, false);
    }

    private static async Task VerifyRecoveryCapacityAsync(HttpClient client, IServiceProvider services, bool isPersistent)
    {
        var path = new Uri("/api/identity/audit-deliveries/recovery-capacity", UriKind.Relative);
        using var diagnostic = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, diagnostic.StatusCode);
        var initial = (await diagnostic.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("identity", initial.GetProperty("source").GetString());
        Assert.Equal(isPersistent, initial.GetProperty("isPersistent").GetBoolean());
        var capacity = initial.GetProperty("capacity");
        Assert.Equal("1000", capacity.GetProperty("maxRecords").GetString());
        Assert.Equal("16777216", capacity.GetProperty("maxPayloadBytes").GetString());
        Assert.Equal(16384, capacity.GetProperty("maxRecordPayloadBytes").GetInt32());
        Assert.Equal("0", capacity.GetProperty("retainedRecords").GetString());
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var facts = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var businessBefore = (await facts.ReadAsync()).Value;
        var policyBefore = (await policies.ReadPolicyAsync()).Value;
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var original = Assert.Single(originals.Take(1));
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
        var request = new
        {
            requestId = Guid.NewGuid(),
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "manual-retry"
        };
        var retry = new Uri($"/api/identity/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(retry,
            new { requestId = request.requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var accepted = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");
        Assert.Equal("1", after.GetProperty("capacity").GetProperty("retainedRecords").GetString());
        Assert.True(after.GetProperty("capacity").GetProperty("retainedPayloadBytes").ReadHttpInt64() > 0);
        Assert.Equal(new[] { "capacity", "isPersistent", "source" },
            after.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using var replay = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(after, (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data")));
        Assert.Equal(businessBefore, (await facts.ReadAsync()).Value);
        Assert.Equal(policyBefore, (await policies.ReadPolicyAsync()).Value);
    }

    [Fact]
    public async Task MemoryRecovery_PreservesTheCallersUncommittedIdentityWork_AndDoesNotPublishIt()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPendingIdentityWorkAsync(app.Services, PlatformAppWithRootAccount.RootUserName);
    }

    [PostgresFact]
    public async Task PostgresRecovery_PreservesTheCallersUncommittedIdentityWork_AndDoesNotPublishIt()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "pending-work-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "pending-work-root-password");
        await VerifyPendingIdentityWorkAsync(app.Services, "journey-root");
    }

    [PostgresFact]
    public async Task PostgresHttp_OriginalRecoveryReceiptSurvivesActualSourceProcessRestart_AndReplaysWithoutChangingTheMessage()
    {
        await using var database = await databases.CreateAsync();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNexusStackPostgres(database.ConnectionString, IdentityDbContext.SchemaName).Options;
        var requestId = Guid.NewGuid();
        DateTimeOffset stoppedAt;
        Guid messageId;
        JsonElement receipt;
        await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "identity-recovery-password"))
        {
            await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "identity-recovery-password");
            await using var source = new IdentityDbContext(options);
            var outbox = new EfOutboxStore<IdentityDbContext>(source);
            var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
            var original = Assert.Single(originals.Take(1));
            Assert.Equal(IdentityEntityCommittedV1.Name, original.EventName);
            messageId = original.Id;
            stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
            var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
            Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
            using var recovered = await first.Client.PostAsJsonAsync(
                new Uri($"/api/identity/audit-deliveries/{messageId}/retry", UriKind.Relative), new
                {
                    requestId,
                    expectedDeadLetteredAt = stoppedAt,
                    expectedRetryRevision = "0",
                    reason = "dependency-restored",
                });
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            receipt = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
            Assert.Equal("identity", receipt.GetProperty("source").GetString());
            Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
            var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            Assert.Equal(original.Id, pending.Id);
            Assert.Equal(original.Payload, pending.Payload);
            Assert.Equal(original.OccurredAt, pending.OccurredAt);
            Assert.Equal(1, pending.RetryRevision);
        }
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "identity-recovery-password");
        await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "identity-recovery-password");
        using var found = await restarted.Client.GetAsync(
            new Uri($"/api/identity/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await found.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        await using var after = new IdentityDbContext(options);
        var restoredOutbox = new EfOutboxStore<IdentityDbContext>(after);
        var beforeReplay = Assert.Single(await restoredOutbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        using var replay = await restarted.Client.PostAsJsonAsync(
            new Uri($"/api/identity/audit-deliveries/{messageId}/retry", UriKind.Relative), new
            {
                requestId,
                expectedDeadLetteredAt = stoppedAt,
                expectedRetryRevision = "0",
                reason = "dependency-restored",
            });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        Assert.Equal(beforeReplay, Assert.Single(await restoredOutbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == messageId));
    }

    [Fact]
    public async Task MemoryHttp_RecoveryUsesTrustedIdentitySource_AndReplaysOriginalReceiptWithoutReopeningLaterBudget()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var original = Assert.Single(originals.Take(1));
        Assert.Equal(IdentityEntityCommittedV1.Name, original.EventName);
        var stoppedAt = DateTimeOffset.UtcNow;
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
        var requestId = Guid.NewGuid();
        var path = new Uri($"/api/identity/audit-deliveries/{original.Id}/retry", UriKind.Relative);
        var request = new
        {
            requestId,
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "manual-retry",
            actorId = "forged-operator",
            source = "platform",
            recoveredAt = DateTimeOffset.MinValue,
        };
        var earliest = DateTimeOffset.UtcNow;
        using var recovered = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(original.Id, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal("identity", receipt.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("actorId").GetString()));
        Assert.NotEqual("forged-operator", receipt.GetProperty("actorId").GetString());
        Assert.Equal("0", receipt.GetProperty("expectedRetryRevision").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        Assert.InRange(receipt.GetProperty("recoveredAt").GetDateTimeOffset(), earliest, DateTimeOffset.UtcNow);
        Assert.Equal(receipt.GetProperty("recoveredAt").GetDateTimeOffset().AddDays(7),
            receipt.GetProperty("retainUntil").GetDateTimeOffset());
        Assert.False(receipt.TryGetProperty("payload", out _));
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
        Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "later-controlled-failure", stoppedAt, 1));
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        using var found = await client.GetAsync(new Uri($"/api/identity/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await found.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
    }

    private static async Task VerifyPendingIdentityWorkAsync(IServiceProvider services, string rootUserName)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await users.FindByUserNameAsync(UserName.Create(rootUserName).Value);
        Assert.NotNull(user);
        var originalSessionVersion = user.SessionVersion;
        var originalVersion = user.Version;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var target = Assert.Single(originals.Take(1));
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
        user.RevokeSessions();
        Assert.Equal(originalSessionVersion + 1, user.SessionVersion);
        Assert.Equal(originalVersion + 1, user.Version);
        await using (var beforeRecovery = services.CreateAsyncScope())
        {
            var baseline = await beforeRecovery.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id);
            Assert.NotNull(baseline);
            Assert.Equal(originalSessionVersion, baseline.SessionVersion);
            Assert.Equal(originalVersion, baseline.Version);
        }
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var accepted = await delivery.RecoverAsync(new(Guid.NewGuid(), target.Id, stoppedAt, 0, "manual-retry"),
            "pending-work-operator", stoppedAt, null);
        Assert.True(accepted.IsSuccess);
        var working = await users.FindAsync(user.Id);
        Assert.NotNull(working);
        Assert.Equal(originalSessionVersion + 1, working.SessionVersion);
        Assert.Equal(originalVersion + 1, working.Version);
        await using (var observer = services.CreateAsyncScope())
        {
            var committed = await observer.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id);
            Assert.NotNull(committed);
            Assert.Equal(originalSessionVersion, committed.SessionVersion);
            Assert.Equal(originalVersion, committed.Version);
        }
        var recovered = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, recovered.Id);
        Assert.Equal(target.Payload, recovered.Payload);
        Assert.Equal(target.OccurredAt, recovered.OccurredAt);
        Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(accepted.Value.RequestId)).Value);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync() > 0);
        await using var after = services.CreateAsyncScope();
        var saved = await after.ServiceProvider.GetRequiredService<IUserRepository>().FindAsync(user.Id);
        Assert.NotNull(saved);
        Assert.Equal(originalSessionVersion + 1, saved.SessionVersion);
        Assert.Equal(originalVersion + 1, saved.Version);
    }
}
