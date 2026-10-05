using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactDeliveryRecoveryCapacityTests
{
    [Fact]
    public async Task Memory_OversizeEvidenceAndFullBytePoolRejectAtomically_WhileReplayAndExpiryPreserveAccounting()
    {
        await using var app = new RecoveryControlApp(1000, 1024, 1024) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.bytes-first").Value, "first")).IsSuccess);
        Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.bytes-second").Value, "second")).IsSuccess);
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        foreach (var original in originals)
        { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-byte-stop", now, 0)); }
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[0].Id, now, 0, "manual-retry");
        var secondRequest = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[1].Id, now, 0, "manual-retry");
        var empty = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1000, empty.Capacity.MaxRecords);
        Assert.Equal(1024, empty.Capacity.MaxPayloadBytes);
        Assert.Equal(1024, empty.Capacity.MaxRecordPayloadBytes);
        var businessBefore = (await business.ReadAsync()).Value;
        var stoppedBefore = await delivery.ListAsync("DeadLettered", 10);
        Assert.Equal(2, stoppedBefore.Count);

        // Both actors have 200 characters; the escaped non-ASCII receipt exceeds the byte envelope.
        var oversized = await delivery.RecoverAsync(request, new string('操', 200), now, null);
        Assert.Equal("platform.delivery_recovery.exhausted", oversized.Error.Code);
        Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(stoppedBefore, await delivery.ListAsync("DeadLettered", 10));
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
        var actor = new string('a', 200);
        var accepted = await delivery.RecoverAsync(request, actor, now, null);
        Assert.True(accepted.IsSuccess);
        var counted = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, counted.Capacity.RetainedRecords);
        Assert.InRange(counted.Capacity.RetainedPayloadBytes, 513, 1024);
        Assert.Equal(originals[0].Id, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);

        var exhausted = await delivery.RecoverAsync(secondRequest, actor, now, null);
        Assert.Equal("platform.delivery_recovery.exhausted", exhausted.Error.Code);
        Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(originals[1].Id, Assert.Single(await delivery.ListAsync("DeadLettered", 10)).MessageId);
        Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(secondRequest.RequestId)).Error.Code);
        Assert.Equal(accepted.Value, (await delivery.RecoverAsync(request, actor, now.AddDays(1), null)).Value);
        Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(0, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7).AddTicks(-1)));
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
        Assert.Equal(empty, (await delivery.ReadRecoveryCapacityAsync()).Value);
        var afterExpiry = await delivery.RecoverAsync(secondRequest, actor, now.AddDays(7), null);
        Assert.True(afterExpiry.IsSuccess);
        Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Count);
        Assert.Equal(businessBefore, (await business.ReadAsync()).Value);
    }

    [Fact]
    public async Task Memory_FullRecoveryPoolRejectsAtomically_ReplaysAndAcceptsAfterCleanup()
    {
        await using var app = new RecoveryControlApp(1) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var businessCapacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.full-first").Value, "first")).IsSuccess);
        Assert.True((await settings.WriteAsync(SettingKey.Create("recovery.full-second").Value, "second")).IsSuccess);
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var first = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[0].Id, now, 0, "manual-retry");
        var second = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals[1].Id, now, 0, "manual-retry");
        var accepted = await delivery.RecoverAsync(first, "quota-operator", now, null);
        Assert.True(accepted.IsSuccess);
        var before = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var beforeBusiness = (await businessCapacity.ReadAsync()).Value;
        var rejected = await delivery.RecoverAsync(second, "quota-operator", now, null);
        Assert.True(rejected.IsFailure);
        Assert.Equal("platform.delivery_recovery.exhausted", rejected.Error.Code);
        Assert.Equal(1, before.Capacity.MaxRecords);
        Assert.Equal(before, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(second.RequestId)).Error.Code);
        Assert.Equal(originals[1].Id, Assert.Single(await delivery.ListAsync("DeadLettered", 10)).MessageId);
        Assert.Equal(originals[0].Id, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);
        var replay = await delivery.RecoverAsync(first, "quota-operator", now.AddDays(1), null);
        Assert.True(replay.IsSuccess);
        Assert.Equal(accepted.Value, replay.Value);
        Assert.Equal(before, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.Equal(1, await delivery.CleanupRecoveriesAsync(1, now.AddDays(7)));
        var released = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(0, released.Capacity.RetainedRecords);
        Assert.Equal(0, released.Capacity.RetainedPayloadBytes);
        Assert.True((await delivery.RecoverAsync(second, "quota-operator", now.AddDays(7), null)).IsSuccess);
        Assert.Equal(1, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Count);
        Assert.Equal(beforeBusiness, (await businessCapacity.ReadAsync()).Value);
    }

    private sealed class RecoveryControlApp(long maxRecords, long maxBytes = 16 * 1024 * 1024, int maxSingleBytes = 16 * 1024) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:AuditDelivery:MemoryRecoveryControl:MaxRecords"] = maxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Platform:AuditDelivery:MemoryRecoveryControl:MaxPayloadBytes"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Platform:AuditDelivery:MemoryRecoveryControl:MaxRecordPayloadBytes"] = maxSingleBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Platform:AuditDelivery:RecoveryMaintenance:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }
    }
}
