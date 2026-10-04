using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactCapacityConcurrencyTests
{
    [Theory]
    [InlineData("platform", false)]
    [InlineData("identity", false)]
    [InlineData("files", false)]
    [InlineData("scheduling", false)]
    [InlineData("platform", true)]
    [InlineData("identity", true)]
    [InlineData("files", true)]
    [InlineData("scheduling", true)]
    public async Task LastCapacitySlot_HasOneWinner_AndCleanupRetainsPendingAndDeadLetters(string context, bool raceCleanup)
    {
        var errorCode = context == "scheduling" ? "scheduling.audit_capacity_exhausted" : $"{context}.audit_capacity.exhausted";
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        await using var app = new CapacityApp(context, clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(context);
        Assert.True((await WriteAsync(app.Services, context, 99141, clock.UtcNow)).IsSuccess);
        var pending = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.True((await WriteAsync(app.Services, context, 99142, clock.UtcNow)).IsSuccess);
        var stopped = Assert.Single((await outbox.ReadPendingAsync(10, clock.UtcNow)).Except([pending]));
        Assert.True(await outbox.MarkDeadLetteredAsync(stopped.Id, "capacity-test", clock.UtcNow, stopped.RetryRevision));
        Assert.True((await WriteAsync(app.Services, context, 99143, clock.UtcNow)).IsSuccess);
        var delivered = Assert.Single((await outbox.ReadPendingAsync(10, clock.UtcNow)).Except([pending]));
        await outbox.MarkDeliveredAsync(delivered.Id, clock.UtcNow);
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(errorCode, (await WriteAsync(app.Services, context, 99144, clock.UtcNow)).Error.Code);

        clock.UtcNow = clock.UtcNow.AddDays(8);
        if (!raceCleanup) { Assert.Equal(1, await cleanup.CleanupAsync()); }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var id = 99150 + index;
            return (Id: id, Result: await WriteAsync(app.Services, context, id, clock.UtcNow));
        })).ToArray();
        var cleaning = raceCleanup ? Task.Run(async () => { await start.Task; return await cleanup.CleanupAsync(); }) : Task.FromResult(0);
        start.SetResult();
        var outcomes = await Task.WhenAll(writes);
        Assert.Equal(raceCleanup ? 1 : 0, await cleaning);
        Assert.InRange(outcomes.Count(item => item.Result.IsSuccess), raceCleanup ? 0 : 1, 1);
        foreach (var outcome in outcomes)
        {
            Assert.Equal(outcome.Result.IsSuccess, await ExistsAsync(app.Services, context, outcome.Id));
            if (outcome.Result.IsFailure) { Assert.Equal(errorCode, outcome.Result.Error.Code); }
        }
        // If every writer reached the lock before cleanup, a subsequent request must recover the released slot.
        if (outcomes.All(item => item.Result.IsFailure))
        {
            Assert.True((await WriteAsync(app.Services, context, 99160, clock.UtcNow)).IsSuccess);
            Assert.True(await ExistsAsync(app.Services, context, 99160));
        }
        Assert.Equal(errorCode, (await WriteAsync(app.Services, context, 99161, clock.UtcNow)).Error.Code);
        Assert.False(await ExistsAsync(app.Services, context, 99161));
        Assert.Equal(0, await cleanup.CleanupAsync());
        var retained = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, retained.Count);
        Assert.Contains(pending, retained);
        Assert.All(retained, entry => Assert.Equal("中", entry.Payload));
        Assert.True(await ExistsAsync(app.Services, context, 99142));
        Assert.True(await ExistsAsync(app.Services, context, 99143));
    }

    private static async Task<Result> WriteAsync(IServiceProvider services, string context, long id, DateTimeOffset now)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        switch (context)
        {
            case "platform":
                return await provider.GetRequiredService<SettingStore>().WriteAsync(SettingKey.Create($"capacity.{id}").Value, "value");
            case "identity":
                await provider.GetRequiredService<IRoleRepository>().AddAsync(Role.Create(new RoleId(id),
                    RoleCode.Create($"capacity-{id}").Value, RoleName.Create("Capacity").Value));
                try { await provider.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync(); return Result.Success(); }
                catch (IdentityAuditCapacityException) { return Result.Failure(IdentityAuditCapacityException.Exhausted); }
            case "files":
                var file = StoredFile.Register(new StoredFileId(id), FileName.Create("capacity.bin").Value, "application/octet-stream", "owner", now).Value;
                try { await provider.GetRequiredService<IStoredFileRepository>().SaveAsync(file); return Result.Success(); }
                catch (FileAuditCapacityException) { return Result.Failure(FileAuditCapacityException.Error); }
            case "scheduling":
                var plan = ScheduledTask.Create(new ScheduledTaskId(id), TaskCode.Create($"capacity-{id}").Value, TimeSpan.FromHours(1), now,
                    ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
                return await provider.GetRequiredService<IScheduledTaskStore>().AddAsync(plan);
            default: throw new InvalidOperationException("Unknown capacity test context.");
        }
    }

    private static async Task<bool> ExistsAsync(IServiceProvider services, string context, long id)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        return context switch
        {
            "platform" => await provider.GetRequiredService<SettingStore>().GetAsync(SettingKey.Create($"capacity.{id}").Value) is not null,
            "identity" => await provider.GetRequiredService<IRoleRepository>().FindByCodeAsync(RoleCode.Create($"capacity-{id}").Value) is not null,
            "files" => await provider.GetRequiredService<IStoredFileRepository>().FindAsync(new StoredFileId(id)) is not null,
            "scheduling" => await provider.GetRequiredService<IScheduledTaskStore>().FindAsync(new ScheduledTaskId(id)) is not null,
            _ => throw new InvalidOperationException("Unknown capacity test context."),
        };
    }

    private sealed class CapacityApp(string context, MutableClock clock) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{context}:AuditDelivery:MemoryCapacity:MaxRecords"] = "3",
                [$"{context}:AuditDelivery:MemoryCapacity:MaxPayloadBytes"] = "9",
                [$"{context}:AuditDelivery:MemoryCapacity:MaxRecordPayloadBytes"] = "3",
                [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock)
                .AddSingleton<IIntegrationEventSerializer>(new KnownPayloadSerializer()));
        }
    }
}
