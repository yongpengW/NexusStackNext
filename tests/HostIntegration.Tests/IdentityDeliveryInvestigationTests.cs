using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityDeliveryInvestigationTests
{
    [Fact]
    public async Task MemoryHttp_PolicyFactCanBeInvestigated_WithoutChangingItsDeliveryOrCapacity()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await VerifyPolicyInvestigationAsync(client, app.Services);
    }

    [PostgresFact]
    public async Task PostgresHttp_PolicyFactCanBeInvestigated_WithoutChangingItsDeliveryOrCapacity()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "policy-investigation-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "policy-investigation-root-password");
        await VerifyPolicyInvestigationAsync(client, app.Services);
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("Pending", 0)]
    [InlineData("Pending", 101)]
    public async Task MemoryHttp_InvalidInvestigationQueryIsRejected(string state, int limit)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        using var response = await client.GetAsync(new Uri($"/api/identity/audit-deliveries?state={state}&limit={limit}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("identity.delivery_query.invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    private static async Task VerifyPolicyInvestigationAsync(HttpClient client, IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var policy = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var initial = (await policy.ReadPolicyAsync()).Value;
        var adjusted = await policy.AdjustAsync(new(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords - 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment"),
            "policy-investigator", DateTimeOffset.UtcNow, null);
        Assert.True(adjusted.IsSuccess);
        Assert.True(adjusted.Value.Changed);
        Assert.NotNull(adjusted.Value.EventId);
        var target = adjusted.Value.EventId.Value;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var beforePolicy = (await policy.ReadPolicyAsync()).Value;
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var before = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == target);
        var list = (await client.GetFromJsonAsync<JsonElement>(new Uri("/api/identity/audit-deliveries?state=Pending&limit=100", UriKind.Relative)))
            .GetProperty("data");
        var listed = Assert.Single(list.EnumerateArray(), entry => entry.GetProperty("messageId").GetGuid() == target);
        var single = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/identity/audit-deliveries/{target}", UriKind.Relative)))
            .GetProperty("data");
        Assert.True(JsonElement.DeepEquals(listed, single));
        Assert.Equal("Pending", single.GetProperty("state").GetString());
        Assert.False(single.TryGetProperty("payload", out _));
        Assert.False(single.TryGetProperty("lastFailure", out _));
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == target));
        Assert.Equal(beforePolicy, (await policy.ReadPolicyAsync()).Value);
        Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
    }

    [PostgresFact]
    public async Task PostgresHttp_SingleMessageCanBeInvestigatedBeyondTheBoundedList_WithoutExposingItsContents()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "investigation-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "investigation-root-password");
        await using var scope = app.Services.CreateAsyncScope();
        await VerifySingleMessageInvestigationAsync(client, scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity"));
    }

    [Fact]
    public async Task MemoryHttp_SingleMessageCanBeInvestigatedBeyondTheBoundedList_WithoutExposingItsContents()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        await VerifySingleMessageInvestigationAsync(client, scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity"));
    }

    private static async Task VerifySingleMessageInvestigationAsync(HttpClient client, IOutboxStore outbox)
    {
        foreach (var name in new[] { "investigation-first", "investigation-second" })
        {
            using var saved = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                new { userName = name, password = "private-investigation-password" });
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.True(originals.Count >= 2);
        var target = originals[^1].Id;
        using var listed = await client.GetAsync(new Uri("/api/identity/audit-deliveries?state=Pending&limit=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var bounded = (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.NotEqual(target, Assert.Single(bounded.EnumerateArray()).GetProperty("messageId").GetGuid());
        var path = new Uri($"/api/identity/audit-deliveries/{target}", UriKind.Relative);
        using var pending = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingData = (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(target, pendingData.GetProperty("messageId").GetGuid());
        Assert.Equal("Pending", pendingData.GetProperty("state").GetString());
        Assert.Equal("0", pendingData.GetProperty("retryRevision").GetString());
        Assert.Equal(new[] { "attempts", "deadLetteredAt", "messageId", "nextAttemptAt", "retryRevision", "state" },
            pendingData.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(originals.Count, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");
        Assert.Equal("DeadLettered", stopped.GetProperty("state").GetString());
        Assert.Equal(1, stopped.GetProperty("attempts").GetInt32());
        Assert.Equal(now, stopped.GetProperty("deadLetteredAt").GetDateTimeOffset());
        Assert.False(stopped.TryGetProperty("payload", out _));
        Assert.False(stopped.TryGetProperty("lastFailure", out _));
        await outbox.MarkDeliveredAsync(target, now.AddMinutes(1));
        var delivered = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");
        Assert.Equal("Delivered", delivered.GetProperty("state").GetString());
        using var missing = await client.GetAsync(new Uri($"/api/identity/audit-deliveries/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("identity.delivery_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }
}
