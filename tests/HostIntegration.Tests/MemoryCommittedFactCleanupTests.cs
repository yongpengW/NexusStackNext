using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryCommittedFactCleanupTests
{
    [Fact]
    public async Task EnabledWorkers_ReclaimConfirmedCopies_AndExposeSuccessfulMaintenanceCounts()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        await using var app = new CleanupApp(clock, enabled: true) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var owners = new[] { "identity", "platform", "files", "scheduling" };
        foreach (var owner in owners)
        {
            await SeedAsync(scope.ServiceProvider, owner, 0, now);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(owner);
            await outbox.MarkDeliveredAsync(Assert.Single(await outbox.ReadPendingAsync(100, now)).Id, now);
        }
        clock.UtcNow = now.AddHours(1);
        var health = app.Services.GetRequiredService<HealthCheckService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var report = await health.CheckHealthAsync(registration => owners.Any(owner => registration.Name == $"{owner}-fact-cleanup"), timeout.Token);
            Assert.Equal(4, report.Entries.Count);
            if (report.Entries.Values.All(entry => entry.Data.TryGetValue("deletedCopies", out var value) && (long)value == 1)) { break; }
            await Task.Delay(50, timeout.Token);
        }
        foreach (var owner in owners)
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(owner).CleanupAsync());
            await AssertBusinessAsync(scope.ServiceProvider, owner);
        }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("platform")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task ModuleMaintenance_UsesCommittedState_PreservesPendingAndStoppedFacts_AndIgnoresLateConfirmation(string owner)
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        await using var app = new CleanupApp(clock, enabled: false) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(owner);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(owner);
        for (var index = 0; index < 3; index++) { await SeedAsync(scope.ServiceProvider, owner, index, now); }
        var facts = await outbox.ReadPendingAsync(100, now);
        Assert.Equal(3, facts.Count);
        await outbox.MarkDeliveredAsync(facts[0].Id, now);
        Assert.True(await outbox.MarkDeadLetteredAsync(facts[1].Id, "safe-failure", now, 0));
        clock.UtcNow = now.AddHours(1).AddTicks(-1);
        Assert.Equal(0, await cleanup.CleanupAsync());
        clock.UtcNow = now.AddHours(1);
        await Task.Delay(1200);
        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == $"{owner}-fact-cleanup");
        Assert.False((bool)Assert.Single(report.Entries).Value.Data["enabled"]);
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(0, await cleanup.CleanupAsync());
        // 发布器的迟到应答不得重建已清理副本，也不得因缺键抛错。
        await outbox.MarkDeliveredAsync(facts[0].Id, now.AddHours(1));
        Assert.False(await outbox.MarkFailedAsync(facts[0].Id, "late-failure", now.AddHours(1), 0));
        Assert.Equal(facts[2].Id, Assert.Single(await outbox.ReadPendingAsync(100, clock.UtcNow)).Id);
        // 停止投递的副本仍在，后来真实确认后才具备清理资格。
        await outbox.MarkDeliveredAsync(facts[1].Id, clock.UtcNow);
        clock.UtcNow = now.AddHours(2);
        Assert.Equal(1, await cleanup.CleanupAsync());
        await AssertBusinessAsync(scope.ServiceProvider, owner);
    }

    private static async Task SeedAsync(IServiceProvider services, string owner, int index, DateTimeOffset now)
    {
        var id = 88300 + index;
        switch (owner)
        {
            case "identity":
                var hash = PasswordHash.Create(new Pbkdf2PasswordHasher().Hash("cleanup-test-password")).Value;
                await services.GetRequiredService<IUserRepository>().AddAsync(User.Register(new UserId(id), UserName.Create($"cleanup-user-{index}").Value, hash, now));
                await services.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync();
                break;
            case "platform":
                Assert.True((await services.GetRequiredService<SettingStore>().WriteAsync(SettingKey.Create($"cleanup.item{index}").Value, "retained-value")).IsSuccess);
                break;
            case "files":
                await services.GetRequiredService<IStoredFileRepository>().SaveAsync(StoredFile.Register(new StoredFileId(id),
                    FileName.Create($"file-{index}.bin").Value, "application/octet-stream", "owner", now).Value);
                break;
            case "scheduling":
                Assert.True((await services.GetRequiredService<IScheduledTaskStore>().AddAsync(ScheduledTask.Create(new ScheduledTaskId(id),
                    TaskCode.Create($"cleanup-plan-{index}").Value, TimeSpan.FromHours(1), now,
                    ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "owner").Value)).IsSuccess);
                break;
        }
    }

    private static async Task AssertBusinessAsync(IServiceProvider services, string owner)
    {
        var version = owner switch
        {
            "identity" => (await services.GetRequiredService<IUserRepository>().FindAsync(new UserId(88300)))!.Version,
            "platform" => (await services.GetRequiredService<ISettingRepository>().FindAsync(SettingKey.Create("cleanup.item0").Value))!.Version,
            "files" => (await services.GetRequiredService<IStoredFileRepository>().FindAsync(new StoredFileId(88300)))!.Version,
            _ => (await services.GetRequiredService<IScheduledTaskStore>().FindAsync(new ScheduledTaskId(88300)))!.Version,
        };
        Assert.Equal(1, version);
    }

    private sealed class CleanupApp(MutableClock clock, bool enabled) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>();
            foreach (var owner in new[] { "Identity", "Platform", "Files", "Scheduling" })
            {
                settings[$"{owner}:AuditDelivery:Cleanup:Enabled"] = enabled.ToString();
                settings[$"{owner}:AuditDelivery:Cleanup:DeliveredRetention"] = "01:00:00";
                settings[$"{owner}:AuditDelivery:Cleanup:Interval"] = "00:00:01";
            }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        }
    }
}
