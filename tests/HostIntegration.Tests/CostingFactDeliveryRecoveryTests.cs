using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CostingFactDeliveryRecoveryTests
{
    [PostgresFact]
    public Task Http_CostFactRecoveryPreservesCompletedTask_ResultAndBusinessDelivery()
        => VerifyRecoveryAsync(policyFact: false);

    [PostgresFact]
    public Task Http_PolicyFactRecoveryPreservesCompletedTask_ResultAndBusinessDelivery()
        => VerifyRecoveryAsync(policyFact: true);

    private static async Task VerifyRecoveryAsync(bool policyFact)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        using var submitted = await host.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), new
        { requestId = taskId, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        await using var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, null);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var claimed = await sender.SendAsync(new ClaimCostingWork());
        Assert.True(claimed.IsSuccess);
        Assert.NotNull(claimed.Value);
        Assert.Equal(taskId, claimed.Value.TaskId);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(taskId, claimed.Value.Epoch))).Value);
        var costPath = new Uri($"/api/costing/items/{itemId}", UriKind.Relative);
        var taskPath = new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative);
        var costBefore = (await host.Client.GetFromJsonAsync<JsonElement>(costPath)).GetProperty("data").Clone();
        var taskBefore = (await host.Client.GetFromJsonAsync<JsonElement>(taskPath)).GetProperty("data").Clone();
        Assert.Equal(100m, costBefore.GetProperty("unitCost").GetDecimal());
        Assert.Equal("Succeeded", taskBefore.GetProperty("state").GetString());
        Assert.Single(taskBefore.GetProperty("history").EnumerateArray());
        var capacityPath = new Uri("/api/costing/audit-capacity", UriKind.Relative);
        if (policyFact)
        {
            using var adjusted = await host.Client.PutAsJsonAsync(capacityPath,
                new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 100001, 256 * 1024 * 1024, 16 * 1024, "operator-adjustment"));
            Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        }
        var capacityBefore = (await host.Client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data").Clone();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(policyFact ? 4 : 3, originals.Count);
        var businessResult = Assert.Single(originals, entry => entry.EventName == CostCalculatedV1.Name);
        Assert.Equal(taskId, businessResult.Id);
        var target = policyFact ? Assert.Single(originals, entry => entry.EventName == CostingFactCapacityPolicyChangedV1.Name)
            : originals.Last(entry => entry.EventName == CostSheetCommittedV1.Name);
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.Equal(policyFact ? 4 : 3, (await publisher.PublishPendingAsync()).DeadLettered);
        var deliveryPath = new Uri($"/api/costing/tasks/{taskId}/delivery", UriKind.Relative);
        var businessBefore = (await host.Client.GetFromJsonAsync<JsonElement>(deliveryPath)).GetProperty("data").Clone();
        using var listed = await host.Client.GetAsync(new Uri("/api/costing/audit-deliveries?state=DeadLettered&limit=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var first = Assert.Single((await listed.Content.ReadApiDataAsync()).EnumerateArray());
        Assert.NotEqual(target.Id, first.GetProperty("messageId").GetGuid());
        var singlePath = new Uri($"/api/costing/audit-deliveries/{target.Id}", UriKind.Relative);
        var observed = (await host.Client.GetFromJsonAsync<JsonElement>(singlePath)).GetProperty("data");
        Assert.Equal("DeadLettered", observed.GetProperty("state").GetString());
        Assert.Equal("0", observed.GetProperty("retryRevision").GetString());
        Assert.False(observed.TryGetProperty("payload", out _));
        Assert.False(observed.TryGetProperty("lastFailure", out _));
        var requestId = Guid.NewGuid();
        var request = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry", actorId = "forged-operator", source = "pricing" };
        var retryPath = new Uri($"/api/costing/audit-deliveries/{target.Id}/retry", UriKind.Relative);
        using var missingRevision = await host.Client.PostAsJsonAsync(retryPath,
            new { requestId, expectedDeadLetteredAt = stoppedAt, reason = "manual-retry" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        using var missingReceipt = await host.Client.GetAsync(new Uri($"/api/costing/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missingReceipt.StatusCode);
        using var accepted = await host.Client.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadApiDataAsync()).Clone();
        Assert.Equal("costing", receipt.GetProperty("source").GetString());
        Assert.Equal("test-operator", receipt.GetProperty("actorId").GetString());
        Assert.Equal("1", receipt.GetProperty("retryRevision").GetString());
        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        Assert.Equal(target.Id, pending.Id);
        Assert.Equal(target.Payload, pending.Payload);
        Assert.Equal(target.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        using var replay = await host.Client.PostAsJsonAsync(retryPath, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
        Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        using var stale = await host.Client.PostAsJsonAsync(retryPath, request with { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var wrongRead = await host.Client.GetAsync(new Uri($"/api/costing/audit-deliveries/{businessResult.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, wrongRead.StatusCode);
        using var wrongRetry = await host.Client.PostAsJsonAsync(new Uri($"/api/costing/audit-deliveries/{businessResult.Id}/retry", UriKind.Relative),
            request with { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRetry.StatusCode);
        Assert.Equal("costing.delivery_recovery.unmanaged", (await wrongRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.True(JsonElement.DeepEquals(costBefore, (await host.Client.GetFromJsonAsync<JsonElement>(costPath)).GetProperty("data")));
        Assert.True(JsonElement.DeepEquals(taskBefore, (await host.Client.GetFromJsonAsync<JsonElement>(taskPath)).GetProperty("data")));
        Assert.True(JsonElement.DeepEquals(capacityBefore, (await host.Client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data")));
        Assert.True(JsonElement.DeepEquals(businessBefore, (await host.Client.GetFromJsonAsync<JsonElement>(deliveryPath)).GetProperty("data")));
        var receiptPath = new Uri($"/api/costing/audit-deliveries/recoveries/{requestId}", UriKind.Relative);
        Assert.True(JsonElement.DeepEquals(receipt, (await host.Client.GetFromJsonAsync<JsonElement>(receiptPath)).GetProperty("data")));
        var recoveryCapacity = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri("/api/costing/audit-deliveries/recovery-capacity", UriKind.Relative)))
            .GetProperty("data");
        Assert.True(recoveryCapacity.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("1", recoveryCapacity.GetProperty("capacity").GetProperty("retainedRecords").GetString());
        using var originalRetry = await host.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{taskId}/delivery/retry", UriKind.Relative), new { expectedDeadLetteredAt = stoppedAt });
        Assert.Equal(HttpStatusCode.Accepted, originalRetry.StatusCode);
        Assert.Equal(businessResult.Id, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Id);
        Assert.True(JsonElement.DeepEquals(taskBefore, (await host.Client.GetFromJsonAsync<JsonElement>(taskPath)).GetProperty("data")));
    }
}
