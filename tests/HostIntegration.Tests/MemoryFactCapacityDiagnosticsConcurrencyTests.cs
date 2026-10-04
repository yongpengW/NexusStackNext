using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactCapacityDiagnosticsConcurrencyTests
{
    [Theory]
    [InlineData("platform")]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task ConcurrentFactConstruction_NeverExposesUncommittedOccupancy_AndHonorsCancellation(string owner)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var serializer = new KnownPayloadSerializer(new SystemTextJsonIntegrationEventSerializer())
        {
            UseKnownPayload = value =>
            {
                if (value is not (SettingCommittedV1 or IdentityEntityCommittedV1 or StoredFileCommittedV1 or PlanCommittedV1)) { return false; }
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("测试未及时释放事实构造。"); }
                return true;
            },
        };
        await using var baseApp = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(owner);
        var before = await reader.ReadAsync();
        Assert.True(before.IsSuccess);
        Assert.Equal(0, before.Value.RetainedRecords);
        var writing = Task.Run(() => WriteAsync(app.Services, owner));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            var busy = await reader.ReadAsync();
            // 事实可能在提交锁外预先构造；此时可读原快照。若已持提交锁，只能立即报告不可读。
            if (busy.IsSuccess) { Assert.Equal(before.Value, busy.Value); }
            else { Assert.Equal(CommittedFactCapacityErrors.Unavailable, busy.Error); }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        }
        finally
        {
            release.Set();
            await writing;
        }
        var committed = await reader.ReadAsync();
        Assert.True(committed.IsSuccess);
        Assert.Equal(owner, committed.Value.Context);
        Assert.Equal(1, committed.Value.RetainedRecords);
        Assert.Equal(3, committed.Value.RetainedPayloadBytes);
        Assert.Equal(99999, committed.Value.RemainingRecords);
        Assert.Equal(268435453, committed.Value.RemainingPayloadBytes);
    }

    private static async Task WriteAsync(IServiceProvider services, string owner)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        switch (owner)
        {
            case "platform":
                Assert.True((await provider.GetRequiredService<SettingStore>().WriteAsync(SettingKey.Create("diagnostics.blocked").Value, "value")).IsSuccess);
                break;
            case "identity":
                await provider.GetRequiredService<IRoleRepository>().AddAsync(Role.Create(new RoleId(88211),
                    RoleCode.Create("diagnostics-blocked").Value, RoleName.Create("Diagnostics").Value));
                await provider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
                break;
            case "files":
                var file = StoredFile.Register(new StoredFileId(88211), FileName.Create("diagnostics.bin").Value,
                    "application/octet-stream", "owner", DateTimeOffset.UtcNow).Value;
                await provider.GetRequiredService<IStoredFileRepository>().SaveAsync(file);
                break;
            case "scheduling":
                var plan = ScheduledTask.Create(new ScheduledTaskId(88211), TaskCode.Create("diagnostics-blocked").Value,
                    TimeSpan.FromHours(1), DateTimeOffset.UtcNow, ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
                Assert.True((await provider.GetRequiredService<IScheduledTaskStore>().AddAsync(plan)).IsSuccess);
                break;
            default: throw new InvalidOperationException("未知的测试来源。");
        }
    }
}
