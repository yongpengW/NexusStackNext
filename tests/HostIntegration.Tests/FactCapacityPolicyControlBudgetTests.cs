using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyControlBudgetTests
{
    [Theory]
    [InlineData("Platform")]
    [InlineData("Identity")]
    [InlineData("Files")]
    [InlineData("Scheduling")]
    public async Task Memory_StartupRejectsInvalidControlLimits_EvenWhenMaintenanceDisabled(string context)
    {
        (long Records, long Bytes, int Single)[] invalidLimits =
        [
            (0, 16 * 1024 * 1024, 16 * 1024),
            (1001, 16 * 1024 * 1024, 16 * 1024),
            (1000, 0, 16 * 1024),
            (1000, 16 * 1024 * 1024 + 1, 16 * 1024),
            (1000, 16 * 1024 * 1024, 0),
            (1000, 16 * 1024 * 1024, 16 * 1024 + 1),
            (1000, 1024, 1025),
        ];
        foreach (var limits in invalidLimits)
        {
            await using var app = new ControlApp(context, limits.Bytes, limits.Single, limits.Records) { SchedulingWorkerEnabled = false };
            var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
            Assert.Equal("内存策略控制额度只能在默认上限内缩小，且单条不得超过总量。", error.Message);
        }
    }

    [Theory]
    [InlineData("platform", "Platform")]
    [InlineData("identity", "Identity")]
    [InlineData("files", "Files")]
    [InlineData("scheduling", "Scheduling")]
    public async Task Memory_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState(string context, string configurationContext)
    {
        IIntegrationEventSerializer serializer = new SystemTextJsonIntegrationEventSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await using var baseApp = new ControlApp(configurationContext, 16 * 1024 * 1024, 2800) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(serializer)));
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var initial = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2800, initial.ControlCapacity.MaxRecordPayloadBytes);
        var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000003"), 1,
            initial.MaxRecords + 1, initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var rejected = await policies.AdjustAsync(request, new string('界', 200), now, null);
        Assert.True(rejected.IsFailure);
        Assert.Equal($"{context}.audit_policy.control_exhausted", rejected.Error.Code);
        Assert.Equal(initial, (await policies.ReadPolicyAsync()).Value);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));

        var retry = await policies.AdjustAsync(request, "test-operator", now, null);
        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value.Changed);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, after.PolicyRevision);
        Assert.Equal(initial.MaxRecords + 1, after.MaxRecords);
        Assert.Equal(initial.RetainedRecords, after.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, after.RetainedPayloadBytes);
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        Assert.InRange(after.ControlCapacity.RetainedPayloadBytes, 1, 2800);
        Assert.Equal(retry.Value.EventId, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);
    }

    [Theory]
    [InlineData("platform", "Platform", false)]
    [InlineData("identity", "Identity", false)]
    [InlineData("files", "Files", false)]
    [InlineData("scheduling", "Scheduling", false)]
    [InlineData("platform", "Platform", true)]
    [InlineData("identity", "Identity", true)]
    [InlineData("files", "Files", true)]
    [InlineData("scheduling", "Scheduling", true)]
    public async Task Memory_StartupControlCountOrByteLimit_RejectsAtomicallyAndReleasesForRetry(string context,
        string configurationContext, bool recordsLimited)
    {
        await using var app = new ControlApp(configurationContext, recordsLimited ? 16 * 1024 * 1024 : 1024,
            recordsLimited ? 16 * 1024 : 1024, recordsLimited ? 1 : 1000)
        { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>(context);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var initial = (await policies.ReadPolicyAsync()).Value;
        Assert.False(initial.IsPersistent);
        Assert.Equal(recordsLimited ? 1 : 1000, initial.ControlCapacity.MaxRecords);
        Assert.Equal(recordsLimited ? 16 * 1024 * 1024 : 1024, initial.ControlCapacity.MaxPayloadBytes);
        Assert.Equal(recordsLimited ? 16 * 1024 : 1024, initial.ControlCapacity.MaxRecordPayloadBytes);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var first = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1,
            initial.MaxRecords, initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(first, "test-operator", now, null);
        Assert.True(accepted.IsSuccess);
        Assert.False(accepted.Value.Changed);
        var retained = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, retained.ControlCapacity.RetainedRecords);
        if (recordsLimited)
        {
            Assert.InRange(retained.ControlCapacity.RetainedPayloadBytes, 1, 1024);
            Assert.Equal(retained.ControlCapacity.MaxRecords, retained.ControlCapacity.RetainedRecords);
        }
        else
        {
            Assert.InRange(retained.ControlCapacity.RetainedPayloadBytes, 513, 1024);
            Assert.True(retained.ControlCapacity.RetainedRecords < retained.ControlCapacity.MaxRecords);
        }
        var second = first with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var rejected = await policies.AdjustAsync(second, "test-operator", now, null);
        Assert.True(rejected.IsFailure);
        Assert.Equal($"{context}.audit_policy.control_exhausted", rejected.Error.Code);
        Assert.Equal(retained, (await policies.ReadPolicyAsync()).Value);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(accepted.Value, (await policies.AdjustAsync(first, "test-operator", now.AddDays(1), null)).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
        var released = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, released.ControlCapacity.RetainedRecords);
        Assert.Equal(0, released.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(1, released.PolicyRevision);
        Assert.Equal(initial.MaxRecords, released.MaxRecords);
        Assert.Equal(initial.MaxPayloadBytes, released.MaxPayloadBytes);
        Assert.Equal(initial.RetainedRecords, released.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, released.RetainedPayloadBytes);
        var retried = await policies.AdjustAsync(second, "test-operator", now.AddDays(7), null);
        Assert.True(retried.IsSuccess);
        Assert.False(retried.Value.Changed);
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
    }

    private sealed class ControlApp(string context, long maxBytes, int maxSingleBytes, long maxRecords = 1000) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryPolicyControl:MaxRecords"] = maxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{context}:AuditDelivery:MemoryPolicyControl:MaxPayloadBytes"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{context}:AuditDelivery:MemoryPolicyControl:MaxRecordPayloadBytes"] = maxSingleBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            return base.CreateHost(builder);
        }
    }
}
