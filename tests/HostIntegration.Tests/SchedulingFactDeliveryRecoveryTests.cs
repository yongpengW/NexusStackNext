using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingFactDeliveryRecoveryTests
{
    [PostgresFact]
    public async Task PostgresHttp_PolicyFactRecoveryAndExpiryPreserveThePlan_HistoryAndBothFactPools()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "scheduling-policy-recovery-root", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "scheduling-policy-recovery-root");
        await VerifyPolicyRecoveryAsync(client, app.Services);
    }

    [Fact]
    public async Task MemoryHttp_PolicyFactRecoveryAndExpiryPreserveThePlan_HistoryAndBothFactPools()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPolicyRecoveryAsync(client, app.Services);
    }

    private static async Task VerifyPolicyRecoveryAsync(HttpClient client, IServiceProvider services)
    {
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "policy-fact-recovery-plan",
            intervalSeconds = 30,
            firstRunInSeconds = 0,
            targetKind = "costing.recalculate",
            targetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        await using var scope = services.CreateAsyncScope();
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Triggered);
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("scheduling");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var changed = await policies.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"), "policy-recovery-operator", DateTimeOffset.UtcNow, null);
        Assert.True(changed.IsSuccess);
        Assert.NotNull(changed.Value.EventId);
        var target = changed.Value.EventId.Value;
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforePolicies = (await policies.ReadPolicyAsync()).Value;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(4, (await publisher.PublishPendingAsync()).DeadLettered);
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var decisions = (await plans.ReadDecisionsAsync(planId, 0, 10)).Items;
        var occurrences = (await plans.ReadOccurrencesAsync(planId, 0, 10)).Items;
        Assert.Single(decisions);
        Assert.Single(occurrences);
        var planPath = new Uri("/api/scheduling/tasks/", UriKind.Relative);
        var planBefore = (await client.GetFromJsonAsync<JsonElement>(planPath)).GetProperty("data").Clone();
        var requestId = Guid.NewGuid();
        var request = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "dependency-restored" };
        var path = new Uri($"/api/scheduling/audit-deliveries/{target}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(path,
            new { requestId = requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var recovered = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = (await recovered.Content.ReadApiDataAsync()).Clone();
        Assert.Equal(target, receipt.GetProperty("messageId").GetGuid());
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        var delivery = scope.ServiceProvider.GetRequiredService<ISchedulingAuditDelivery>();
        var state = (await delivery.GetAsync(target)).Value;
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, receipt.GetProperty("retainUntil").GetDateTimeOffset().AddTicks(10)));
        Assert.Equal(state, (await delivery.GetAsync(target)).Value);
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(decisions, (await plans.ReadDecisionsAsync(planId, 0, 10)).Items);
        Assert.Equal(occurrences, (await plans.ReadOccurrencesAsync(planId, 0, 10)).Items);
        Assert.True(JsonElement.DeepEquals(planBefore, (await client.GetFromJsonAsync<JsonElement>(planPath)).GetProperty("data")));
    }

    [PostgresFact]
    public async Task PostgresHttp_RecoversOnlyTheStoppedPlanFact_AndPreservesNonemptyTriggerHistory()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "scheduling-recovery-root", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "scheduling-recovery-root");
        await VerifyPlanFactRecoveryAsync(client, app.Services);
    }

    [Fact]
    public async Task MemoryHttp_RecoversOnlyTheStoppedPlanFact_AndPreservesNonemptyTriggerHistory()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPlanFactRecoveryAsync(client, app.Services);
    }

    private static async Task VerifyPlanFactRecoveryAsync(HttpClient client, IServiceProvider services)
    {
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "fact-recovery-plan",
            intervalSeconds = 30,
            firstRunInSeconds = 0,
            targetKind = "costing.recalculate",
            targetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        await using var scope = services.CreateAsyncScope();
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Triggered);
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var decisions = (await plans.ReadDecisionsAsync(planId, 0, 10)).Items;
        Assert.Single(decisions);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(3, originals.Count);
        var occurrence = Assert.Single(originals, entry => entry.EventName == ScheduleTriggeredV1.Name);
        var target = originals.Last(entry => entry.EventName == PlanCommittedV1.Name);
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(3, (await publisher.PublishPendingAsync()).DeadLettered);
        var occurrences = (await plans.ReadOccurrencesAsync(planId, 0, 10)).Items;
        Assert.Single(occurrences);
        var planPath = new Uri("/api/scheduling/tasks/", UriKind.Relative);
        var planBefore = (await client.GetFromJsonAsync<JsonElement>(planPath)).GetProperty("data").Clone();
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var beforeBusiness = (await business.ReadAsync()).Value;
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("scheduling");
        var beforePolicy = (await policies.ReadPolicyAsync()).Value;
        using var listed = await client.GetAsync(new Uri("/api/scheduling/audit-deliveries?state=DeadLettered&limit=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var first = Assert.Single((await listed.Content.ReadApiDataAsync()).EnumerateArray());
        Assert.NotEqual(target.Id, first.GetProperty("messageId").GetGuid());
        var singlePath = new Uri($"/api/scheduling/audit-deliveries/{target.Id}", UriKind.Relative);
        var observed = (await client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data");
        Assert.Equal("DeadLettered", observed.GetProperty("state").GetString());
        Assert.Equal("0", observed.GetProperty("retryRevision").GetString());
        Assert.False(observed.TryGetProperty("payload", out _));
        Assert.False(observed.TryGetProperty("lastFailure", out _));
        var requestId = Guid.NewGuid();
        var request = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" };
        var path = new Uri($"/api/scheduling/audit-deliveries/{target.Id}/retry", UriKind.Relative);
        using var missingRevision = await client.PostAsJsonAsync(path,
            new { requestId = requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        using var accepted = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadApiDataAsync()).Clone();
        Assert.Equal("scheduling", receipt.GetProperty("source").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, pending.Id);
        Assert.Equal(target.Payload, pending.Payload);
        Assert.Equal(target.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        using var stale = await client.PostAsJsonAsync(path, request with { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var unmanaged = await client.GetAsync(new Uri($"/api/scheduling/audit-deliveries/{occurrence.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unmanaged.StatusCode);
        using var wrongRetry = await client.PostAsJsonAsync(new Uri($"/api/scheduling/audit-deliveries/{occurrence.Id}/retry", UriKind.Relative),
            request with { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRetry.StatusCode);
        Assert.Equal("scheduling.delivery_recovery.unmanaged", (await wrongRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
        Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(decisions, (await plans.ReadDecisionsAsync(planId, 0, 10)).Items);
        Assert.Equal(occurrences, (await plans.ReadOccurrencesAsync(planId, 0, 10)).Items);
        Assert.True(JsonElement.DeepEquals(planBefore, (await client.GetFromJsonAsync<JsonElement>(planPath)).GetProperty("data")));
        var receiptPath = new Uri($"/api/scheduling/audit-deliveries/recoveries/{requestId}", UriKind.Relative);
        Assert.True(JsonElement.DeepEquals(receipt, (await client.GetFromJsonAsync<JsonElement>(receiptPath)).GetProperty("data")));
        var capacityPath = new Uri("/api/scheduling/audit-deliveries/recovery-capacity", UriKind.Relative);
        Assert.Equal("1", (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data").GetProperty("capacity").GetProperty("retainedRecords").GetString());
        using var occurrenceRetry = await client.PostAsJsonAsync(new Uri($"/api/scheduling/occurrences/{occurrence.Id}/retry", UriKind.Relative),
            new { expectedDeadLetteredAt = stoppedAt });
        Assert.Equal(HttpStatusCode.Accepted, occurrenceRetry.StatusCode);
        Assert.Equal(occurrence.Id, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Id);
        Assert.Equal(decisions, (await plans.ReadDecisionsAsync(planId, 0, 10)).Items);
        Assert.True(JsonElement.DeepEquals(planBefore, (await client.GetFromJsonAsync<JsonElement>(planPath)).GetProperty("data")));
    }
}
