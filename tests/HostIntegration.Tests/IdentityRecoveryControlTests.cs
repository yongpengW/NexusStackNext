using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Identity.Application;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityRecoveryControlTests
{
    [Fact]
    public async Task MemoryHttp_FullRecoveryPoolRejectsAtomically_ReplaysAndAcceptsAfterCleanup()
    {
        await using var app = new RecoveryControlApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName,
            PlatformAppWithRootAccount.RootPassword);
        var capacityPath = new Uri("/api/identity/audit-deliveries/recovery-capacity", UriKind.Relative);
        var initial = (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data");
        Assert.Equal("1", initial.GetProperty("capacity").GetProperty("maxRecords").GetString());
        using var created = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
            new { userName = "recovery-quota-user", password = "recovery-quota-password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var originals = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Take(2).ToArray();
        Assert.Equal(2, originals.Length);
        var stoppedAt = DateTimeOffset.UtcNow;
        foreach (var original in originals)
        { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-quota-stop", stoppedAt, 0)); }
        var firstRequest = new
        {
            requestId = Guid.NewGuid(),
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "manual-retry"
        };
        var secondRequest = new
        {
            requestId = Guid.NewGuid(),
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "manual-retry"
        };
        var firstPath = new Uri($"/api/identity/audit-deliveries/{originals[0].Id}/retry", UriKind.Relative);
        var secondPath = new Uri($"/api/identity/audit-deliveries/{originals[1].Id}/retry", UriKind.Relative);
        using var accepted = await client.PostAsJsonAsync(firstPath, firstRequest);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var full = (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data");
        using var rejected = await client.PostAsJsonAsync(secondPath, secondRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("identity.delivery_recovery.exhausted",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal("identity.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(secondRequest.requestId)).Error.Code);
        Assert.True(JsonElement.DeepEquals(full, (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data")));
        Assert.DoesNotContain(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == originals[1].Id);
        using var replay = await client.PostAsJsonAsync(firstPath, firstRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(receipt, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")));
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, receipt.GetProperty("retainUntil").GetDateTimeOffset()));
        using var afterCleanup = await client.PostAsJsonAsync(secondPath, secondRequest);
        Assert.Equal(HttpStatusCode.OK, afterCleanup.StatusCode);
        Assert.Equal("1", (await client.GetFromJsonAsync<JsonElement>(capacityPath)).GetProperty("data")
            .GetProperty("capacity").GetProperty("retainedRecords").GetString());
        Assert.Contains(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == originals[1].Id);
    }

    private sealed class RecoveryControlApp : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:AuditDelivery:MemoryRecoveryControl:MaxRecords"] = "1",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
            }));
        }
    }
}
