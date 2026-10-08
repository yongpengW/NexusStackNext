using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactWriteBudgetTests
{
    [Theory]
    [InlineData("platform")]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task OmittedWriteBudget_UsesFiniteThreeSecondDefault(string context)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, context, timeout: null) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(context);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var now = clock.UtcNow;
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? reading = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = Stopwatch.GetTimestamp();
            reading = Task.Run(() => outbox.ReadPendingAsync(10, now));
            await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 2.5, 5.5);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (reading is not null)
            {
                try { await reading; }
                catch (CommittedFactCapacityBusyException) { }
            }
        }
        Assert.Empty(await outbox.ReadPendingAsync(10, now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchedulingApplicationWrite_ReportsBusyDuringRead_AndPreservesPlanVersion(bool changingRule)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Scheduling") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TaskRegistry>();
        var plan = await registry.DefineAsync(TaskCode.Create("application-budget").Value, TimeSpan.FromHours(1),
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42");
        Assert.True(plan.IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var before = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var rule = new ScheduleRuleInput("Interval", IntervalSeconds: 7200);
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var attempt = Task.Run(() => changingRule ? registry.UpdateRuleAsync(plan.Value.Id, 1, rule) : registry.PauseAsync(plan.Value.Id, 1));
            writing = attempt;
            Assert.Equal("audit_capacity.busy", (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null)
            {
                try { await writing; }
                catch (CommittedFactCapacityBusyException) { }
            }
        }
        var unchanged = await registry.FindAsync(plan.Value.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.Version);
        Assert.True(unchanged.IsEnabled);
        Assert.Equal(TimeSpan.FromHours(1), unchanged.Interval);
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));
        var recovered = changingRule ? await registry.UpdateRuleAsync(plan.Value.Id, 1, rule) : await registry.PauseAsync(plan.Value.Id, 1);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(2, (await registry.FindAsync(plan.Value.Id))!.Version);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileApplicationResult_ReportsBusyDuringMetadataRead_WithoutStartingDeletion(bool deleting)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Files") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FileService>();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files");
        var file = StoredFile.Register(new StoredFileId(97321), FileName.Create("read-budget.bin").Value, "application/octet-stream", "owner", clock.UtcNow).Value;
        await files.SaveAsync(file);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? reading = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var attempt = Task.Run(async () =>
            {
                if (deleting) { return (NexusStackNext.BuildingBlocks.Domain.Result)await service.DeleteAsync(file.Id, "owner"); }
                return await service.OpenAsync(file.Id, "owner");
            });
            reading = attempt;
            Assert.Equal("audit_capacity.busy", (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (reading is not null)
            {
                try { await reading; }
                catch (CommittedFactCapacityBusyException) { }
            }
        }
        Assert.Equal(1, (await files.FindAsync(file.Id))!.Version);
        Assert.Null(await files.FindDeletedAsync(file.Id));
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));
        if (deleting)
        {
            var completed = await service.DeleteAsync(file.Id, "owner");
            Assert.True(completed.IsSuccess);
            Assert.True(completed.Value);
            Assert.NotNull((await files.FindDeletedAsync(file.Id))!.BytesRemovedAt);
        }
        else { Assert.Equal("files.content_missing", (await service.OpenAsync(file.Id, "owner")).Error.Code); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderQuery_ReportsBusyAsResultOrPropagatesCancellation_ThenReadsCommittedMenuWithoutAddingFacts(bool cancel)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Identity") { SchedulingWorkerEnabled = false };
        await using var seed = app.Services.CreateAsyncScope();
        var created = await seed.ServiceProvider.GetRequiredService<ISender>().SendAsync(new CreateMenuCommand("Budget", 0, null));
        Assert.True(created.IsSuccess);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var before = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, before.Count);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? querying = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempt = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                started.SetResult();
                return await sender.QueryAsync(new GetMenusQuery(), cancellation.Token);
            });
            querying = attempt;
            await started.Task;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else { Assert.Equal("audit_capacity.busy", (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code); }
        }
        finally
        {
            await holder.ReleaseAsync();
            if (querying is not null)
            {
                try { await querying; }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            }
        }
        var recovered = await sender.QueryAsync(new GetMenusQuery());
        Assert.True(recovered.IsSuccess);
        Assert.Equal(created.Value.MenuId, Assert.Single(recovered.Value).MenuId);
        Assert.Equal(before, await outbox.ReadPendingAsync(10, clock.UtcNow));
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task WaitingWriter_PropagatesCallerCancellation_WithoutPublishingAnyFacts(string context)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, context) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(context);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(context);
        var now = clock.UtcNow;
        await using var cancel = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writing = Task.Run(async () =>
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
                started.SetResult();
                switch (context)
                {
                    case "platform":
                        await scope.ServiceProvider.GetRequiredService<SettingStore>().WriteAsync(SettingKey.Create("budget.cancel").Value, "value", cancellationToken: cancel.Token);
                        break;
                    case "identity":
                        await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new CreateRoleCommand("budget-cancel", "Budget"), cancel.Token);
                        break;
                    case "files":
                        var file = StoredFile.Register(new StoredFileId(97223), FileName.Create("cancel.bin").Value, "application/octet-stream", "owner", now).Value;
                        await scope.ServiceProvider.GetRequiredService<IStoredFileRepository>().SaveAsync(file, cancellationToken: cancel.Token);
                        break;
                    case "scheduling":
                        var plan = ScheduledTask.Create(new ScheduledTaskId(97224), TaskCode.Create("budget-cancel").Value, TimeSpan.FromHours(1), now,
                            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
                        await scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>().AddAsync(plan, cancellationToken: cancel.Token);
                        break;
                }
            });
            await started.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null)
            {
                try { await writing; }
                catch (OperationCanceledException) { }
            }
        }
        Assert.Empty(await outbox.ReadPendingAsync(10, now));
        Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedRecords);
        switch (context)
        {
            case "platform": Assert.Null(await scope.ServiceProvider.GetRequiredService<SettingStore>().GetAsync(SettingKey.Create("budget.cancel").Value)); break;
            case "identity": Assert.Null(await scope.ServiceProvider.GetRequiredService<IRoleRepository>().FindByCodeAsync(NexusStackNext.Identity.Domain.Roles.RoleCode.Create("budget-cancel").Value)); break;
            case "files": Assert.Null(await scope.ServiceProvider.GetRequiredService<IStoredFileRepository>().FindAsync(new StoredFileId(97223))); break;
            case "scheduling": Assert.Null(await scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>().FindAsync(new ScheduledTaskId(97224))); break;
        }
    }

    [Fact]
    public async Task HttpAuthorizationRead_WhenIdentityIsBusy_ReturnsStable503_AndRecovers()
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Identity", root: true) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await using var scope = app.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        var holder = new PausedFactCleanup(clock, cleanup);
        Task<HttpResponseMessage>? request = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            request = client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
            using var response = await request.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("identity.session.unavailable", error.GetProperty("errorCode").GetString());
            Assert.Equal(503, error.GetProperty("code").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        }
        finally
        {
            await holder.ReleaseAsync();
            if (request is not null) { (await request).Dispose(); }
        }
        using var recovered = await client.GetAsync(new Uri("/api/identity/menus", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task SchedulingDecision_RefusesBusyWithoutAdvancingPlanOrInventingSkipped_ThenRecoversOnce()
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Scheduling") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var now = clock.UtcNow;
        var plan = ScheduledTask.Create(new ScheduledTaskId(97124), TaskCode.Create("memory-budget").Value, TimeSpan.FromHours(1), now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, now));
        Assert.True(plan.Advance(now, now.AddHours(1), trigger: true).IsSuccess);
        var id = Guid.NewGuid();
        var occurrence = new ScheduleOccurrence(id, plan.Id.Value, plan.TriggerSequence, now, now, plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
        var decision = new ScheduleDecision(id, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule, "Triggered", now, now, now.AddHours(1), id);
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var attempt = Task.Run(() => store.RecordDecisionAsync(plan, 1, decision, occurrence));
            writing = attempt;
            Assert.Equal("audit_capacity.busy", (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null) { await writing; }
        }
        Assert.Equal(1, (await store.FindAsync(plan.Id))!.Version);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(1, (await reader.ReadAsync()).Value.RetainedRecords);
        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.Equal(2, (await store.FindAsync(plan.Id))!.Version);
        Assert.Equal("Triggered", Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items).Kind);
        Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, now)).Count);
        Assert.Equal(2, (await reader.ReadAsync()).Value.RetainedRecords);
    }

    [Fact]
    public async Task FileMetadata_RefusesBusyBeforePublishingStateOrFacts_ThenCommitsOneFact()
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, "Files") { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files");
        var file = StoredFile.Register(new StoredFileId(97123), FileName.Create("budget.bin").Value, "application/octet-stream", "owner", clock.UtcNow).Value;
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            writing = Task.Run(() => files.SaveAsync(file));
            var busy = await Assert.ThrowsAsync<FileAuditCapacityException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("audit_capacity.busy", busy.Reason.Code);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null)
            {
                try { await writing; }
                catch (FileAuditCapacityException) { }
            }
        }
        Assert.Null(await files.FindAsync(file.Id));
        Assert.Empty(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedRecords);
        await files.SaveAsync(file);
        Assert.Equal(1, (await files.FindAsync(file.Id))!.Version);
        Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(1, (await reader.ReadAsync()).Value.RetainedRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityPermissionCommand_RefusesBusyAtReadOrCommit_WithoutDirtyScopeOrCacheInvalidation(bool preload)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        var cache = new RecordingPermissionCache();
        await using var baseApp = new MemoryBudgetApp(clock, "Identity") { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IPermissionCache>(cache)));
        await using var seed = app.Services.CreateAsyncScope();
        var seedSender = seed.ServiceProvider.GetRequiredService<ISender>();
        var user = await seedSender.SendAsync(new CreateUserCommand("memory-budget", "memory-budget-password"));
        var role = await seedSender.SendAsync(new CreateRoleCommand("memory-budget", "Budget"));
        Assert.True(user.IsSuccess);
        Assert.True(role.IsSuccess);
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        if (preload) { Assert.NotNull(await users.FindAsync(new UserId(user.Value))); }
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("identity");
        var before = await outbox.ReadPendingAsync(10, clock.UtcNow);
        var invalidations = cache.Invalidations;
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var attempt = Task.Run(() => sender.SendAsync(new AssignRoleCommand(user.Value, role.Value)));
            writing = attempt;
            Assert.Equal("audit_capacity.busy", (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code);
            Assert.Equal(invalidations, cache.Invalidations);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null) { await writing; }
        }
        var unchanged = await users.FindAsync(new UserId(user.Value));
        Assert.NotNull(unchanged);
        Assert.Empty(unchanged.RoleIds);
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(before, await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(2, (await reader.ReadAsync()).Value.RetainedRecords);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.Equal(invalidations + 1, cache.Invalidations);
        Assert.True((await sender.SendAsync(new AssignRoleCommand(user.Value, role.Value))).IsSuccess);
        Assert.Equal(invalidations + 1, cache.Invalidations);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
        Assert.Equal(3, (await reader.ReadAsync()).Value.RetainedRecords);
    }

    [Fact]
    public async Task Cleanup_RefusesBusyWithoutReleasingCapacity_ThenRemovesConfirmedExpiredFact()
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("platform");
        Assert.True((await store.WriteAsync(SettingKey.Create("budget.cleanup").Value, "value")).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? cleaning = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            cleaning = Task.Run(() => cleanup.CleanupAsync());
            await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await cleaning.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Assert.Equal(0, await holder.ReleaseAsync());
            if (cleaning is not null)
            {
                try { await cleaning; }
                catch (CommittedFactCapacityBusyException) { }
            }
        }
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));
        Assert.Equal(1, (await reader.ReadAsync()).Value.RetainedRecords);
        await outbox.MarkDeliveredAsync(before.Id, clock.UtcNow.AddDays(-8));
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedRecords);
        Assert.Empty(await outbox.ReadPendingAsync(10, clock.UtcNow));
    }

    [Fact]
    public async Task PlatformWrite_RefusesBusyWithinConfiguredBudget_AndRecoversWithoutPartialCommit()
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var key = SettingKey.Create("budget.setting").Value;
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("platform");
        Assert.True((await store.WriteAsync(key, "before")).IsSuccess);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? writing = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var attempt = Task.Run(() => store.WriteAsync(key, "after"));
            writing = attempt;
            var result = await attempt.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("audit_capacity.busy", result.Error.Code);
            var unchanged = Assert.IsType<NexusStackNext.Platform.Domain.Settings.GlobalSetting>(await store.GetAsync(key));
            Assert.Equal("before", unchanged.Value);
            Assert.Equal(1, unchanged.Version);
            Assert.Equal("audit_capacity.unavailable", (await reader.ReadAsync()).Error.Code);
        }
        finally
        {
            await holder.ReleaseAsync();
            if (writing is not null) { await writing; }
        }
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow)));
        Assert.Equal(1, (await reader.ReadAsync()).Value.RetainedRecords);
        Assert.True((await store.WriteAsync(key, "after")).IsSuccess);
        Assert.Equal(2, (await store.GetAsync(key))!.Version);
        Assert.Equal(2, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
        Assert.Equal(2, (await reader.ReadAsync()).Value.RetainedRecords);
    }

}
