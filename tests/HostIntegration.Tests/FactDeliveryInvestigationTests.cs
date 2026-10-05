using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactDeliveryInvestigationTests
{
    [PostgresFact]
    public async Task PostgresSources_UnmanagedMessagesAreExplicitlyRejected_WithoutOpeningTheirBudget()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "unmanaged-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "unmanaged-root-password");
        foreach (var source in new[] { "platform", "identity" })
        {
            await using var scope = app.Services.CreateAsyncScope();
            var original = new OutboxEntry
            {
                Id = Guid.NewGuid(),
                EventName = "unmanaged.external.v1",
                Payload = "private-unmanaged-body",
                OccurredAt = DateTimeOffset.UtcNow
            };
            if (source == "platform")
            {
                var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                context.Outbox.Add(original);
                await context.SaveChangesAsync();
            }
            else
            {
                var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                context.Outbox.Add(original);
                await context.SaveChangesAsync();
            }
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source);
            var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
            Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-unmanaged-failure", stoppedAt, 0));
            var capacityPath = new Uri($"/api/{source}/audit-deliveries/recovery-capacity", UriKind.Relative);
            var before = (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data");
            var requestId = Guid.NewGuid();
            using var rejected = await client.PostAsJsonAsync(
                new Uri($"/api/{source}/audit-deliveries/{original.Id}/retry", UriKind.Relative),
                new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = "0", reason = "manual-retry" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(source + ".delivery_recovery.unmanaged", error.GetProperty("errorCode").GetString());
            using var changedObservation = await client.PostAsJsonAsync(
                new Uri($"/api/{source}/audit-deliveries/{original.Id}/retry", UriKind.Relative),
                new { requestId, expectedDeadLetteredAt = stoppedAt.AddTicks(1), expectedRetryRevision = "0", reason = "manual-retry" });
            Assert.Equal(HttpStatusCode.BadRequest, changedObservation.StatusCode);
            Assert.Equal(source + ".delivery_recovery.unmanaged",
                (await changedObservation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            Assert.DoesNotContain(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
            Assert.True(JsonElement.DeepEquals(before, (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data")));
            using var missing = await client.GetAsync(new Uri($"/api/{source}/audit-deliveries/recoveries/{requestId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var list = (await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/{source}/audit-deliveries?state=DeadLettered&limit=100", UriKind.Relative))).GetProperty("data");
            Assert.DoesNotContain(list.EnumerateArray(), item => item.GetProperty("messageId").GetGuid() == original.Id);
            using var unmanaged = await client.GetAsync(new Uri($"/api/{source}/audit-deliveries/{original.Id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, unmanaged.StatusCode);
            Assert.Equal(source + ".delivery_not_found", (await unmanaged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task PlatformMemory_SingleMessageCanBeInvestigatedBeyondTheBoundedList_WithoutExposingItsContents()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        await VerifySingleMessageInvestigationAsync(client, scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform"));
    }

    [PostgresFact]
    public async Task PlatformPostgres_SingleMessageCanBeInvestigatedBeyondTheBoundedList_WithoutExposingItsContents()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "investigation-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "investigation-root-password");
        await using var scope = app.Services.CreateAsyncScope();
        await VerifySingleMessageInvestigationAsync(client, scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform"));
    }

    private static async Task VerifySingleMessageInvestigationAsync(HttpClient client, IOutboxStore outbox)
    {
        foreach (var key in new[] { "investigation.first", "investigation.second" })
        {
            using var saved = await client.PutAsJsonAsync(new Uri("/api/platform/settings/" + key, UriKind.Relative),
                new { value = "private-business-content" });
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var target = originals[1].Id;
        var bounded = (await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/platform/audit-deliveries?state=Pending&limit=1", UriKind.Relative))).GetProperty("data");
        Assert.NotEqual(target, Assert.Single(bounded.EnumerateArray()).GetProperty("messageId").GetGuid());
        var path = new Uri($"/api/platform/audit-deliveries/{target}", UriKind.Relative);
        using var pending = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingData = (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(target, pendingData.GetProperty("messageId").GetGuid());
        Assert.Equal("Pending", pendingData.GetProperty("state").GetString());
        Assert.Equal("0", pendingData.GetProperty("retryRevision").GetString());
        Assert.Equal(new[] { "attempts", "deadLetteredAt", "messageId", "nextAttemptAt", "retryRevision", "state" },
            pendingData.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var stopped = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");
        Assert.Equal("DeadLettered", stopped.GetProperty("state").GetString());
        Assert.Equal(1, stopped.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.String, stopped.GetProperty("deadLetteredAt").ValueKind);
        Assert.False(stopped.TryGetProperty("payload", out _));
        Assert.False(stopped.TryGetProperty("lastFailure", out _));
        await outbox.MarkDeliveredAsync(target, now.AddMinutes(1));
        var delivered = (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");
        Assert.Equal("Delivered", delivered.GetProperty("state").GetString());
        using var missing = await client.GetAsync(new Uri($"/api/platform/audit-deliveries/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var notFound = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("platform.delivery_not_found", notFound.GetProperty("errorCode").GetString());
    }
}
