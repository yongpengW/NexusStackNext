using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FilesFactDeliveryRecoveryTests
{
    [PostgresFact]
    public async Task PostgresHttp_PolicyFactCanBeRecovered_WithoutChangingTheFileOrEitherFactPool()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-policy-recovery-root", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-policy-recovery-root");
        await VerifyPolicyRecoveryAsync(client, app.Services);
    }

    [Fact]
    public async Task MemoryHttp_PolicyFactCanBeRecovered_WithoutChangingTheFileOrEitherFactPool()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPolicyRecoveryAsync(client, app.Services);
    }

    private static async Task VerifyPolicyRecoveryAsync(HttpClient client, IServiceProvider services)
    {
        using var bytes = new ByteArrayContent([8, 4, 2]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=policy-recovery-private.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var fileId = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        var metadataPath = new Uri($"/api/files/{fileId}/metadata", UriKind.Relative);
        var metadata = (await client.GetFromJsonAsync<JsonElement>(metadataPath)).GetProperty("data").Clone();
        await using var scope = services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var changed = await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-recovery-operator", DateTimeOffset.UtcNow, null);
        Assert.True(changed.IsSuccess);
        Assert.NotNull(changed.Value.EventId);
        var target = changed.Value.EventId.Value;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforePolicies = (await policies.ReadPolicyAsync()).Value;
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(3, (await publisher.PublishPendingAsync()).DeadLettered);
        var original = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/files/audit-deliveries/{target}", UriKind.Relative)))
            .GetProperty("data");
        Assert.Equal("DeadLettered", original.GetProperty("state").GetString());
        Assert.False(original.TryGetProperty("payload", out _));
        var requestId = Guid.NewGuid();
        var request = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" };
        var path = new Uri($"/api/files/audit-deliveries/{target}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(path,
            new { requestId = requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var recovered = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        Assert.Equal(target, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
        Assert.True(JsonElement.DeepEquals(metadata, (await client.GetFromJsonAsync<JsonElement>(metadataPath)).GetProperty("data")));
        Assert.Equal(new byte[] { 8, 4, 2 }, await client.GetByteArrayAsync(new Uri($"/api/files/{fileId}", UriKind.Relative)));
        var delivery = scope.ServiceProvider.GetRequiredService<IFileAuditDelivery>();
        var state = (await delivery.GetAsync(target)).Value;
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, receipt.GetProperty("retainUntil").GetDateTimeOffset().AddTicks(10)));
        Assert.Equal(state, (await delivery.GetAsync(target)).Value);
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
    }

    [PostgresFact]
    public async Task PostgresHttp_RecoversOnlyTheStoppedFact_ReplaysTheDecisionAndPreservesTheFile()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-recovery-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-recovery-root-password");
        await VerifyFileRecoveryAsync(client, app.Services);
    }

    [Fact]
    public async Task MemoryHttp_RecoversOnlyTheStoppedFact_ReplaysTheDecisionAndPreservesTheFile()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyFileRecoveryAsync(client, app.Services);
    }

    private static async Task VerifyFileRecoveryAsync(HttpClient client, IServiceProvider services)
    {
        using var bytes = new ByteArrayContent([8, 4, 2]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=recovery-private.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var fileId = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        var metadataPath = new Uri($"/api/files/{fileId}/metadata", UriKind.Relative);
        var metadata = (await client.GetFromJsonAsync<JsonElement>(metadataPath)).GetProperty("data").Clone();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var target = originals[^1];
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        using var listed = await client.GetAsync(new Uri("/api/files/audit-deliveries?state=DeadLettered&limit=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var first = Assert.Single((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.NotEqual(target.Id, first.GetProperty("messageId").GetGuid());
        var singlePath = new Uri($"/api/files/audit-deliveries/{target.Id}", UriKind.Relative);
        using var single = await client.GetAsync(singlePath);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        var observed = (await single.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("DeadLettered", observed.GetProperty("state").GetString());
        Assert.Equal("0", observed.GetProperty("retryRevision").GetString());
        Assert.False(observed.TryGetProperty("payload", out _));
        Assert.False(observed.TryGetProperty("lastFailure", out _));
        var requestId = Guid.NewGuid();
        var request = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" };
        var path = new Uri($"/api/files/audit-deliveries/{target.Id}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(path,
            new { requestId = requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var recovered = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();
        Assert.Equal(requestId, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal("files", receipt.GetProperty("source").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, pending.Id);
        Assert.Equal(target.Payload, pending.Payload);
        Assert.Equal(target.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        using var stale = await client.PostAsJsonAsync(path,
            new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var readback = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/files/audit-deliveries/recoveries/{requestId}", UriKind.Relative)))
            .GetProperty("data");
        Assert.True(JsonElement.DeepEquals(receipt, readback));
        var capacity = (await client.GetFromJsonAsync<JsonElement>(new Uri("/api/files/audit-deliveries/recovery-capacity", UriKind.Relative)))
            .GetProperty("data").GetProperty("capacity");
        Assert.Equal("1", capacity.GetProperty("retainedRecords").GetString());
        Assert.True(JsonElement.DeepEquals(metadata, (await client.GetFromJsonAsync<JsonElement>(metadataPath)).GetProperty("data")));
        Assert.Equal(new byte[] { 8, 4, 2 }, await client.GetByteArrayAsync(new Uri($"/api/files/{fileId}", UriKind.Relative)));
    }
}
