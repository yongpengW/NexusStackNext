using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Identity.Application;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFactMaintenanceBudgetTests
{
    public static TheoryData<string, bool, bool> TerminalCases
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            foreach (var context in new[] { "platform", "identity", "files", "scheduling" })
            {
                cases.Add(context, false, false);
                cases.Add(context, false, true);
                cases.Add(context, true, false);
                cases.Add(context, true, true);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public async Task MaintenanceRefusal_PreservesConfirmedOrDeadLetteredFact_AndRecoveryKeepsConfirmationPriority(string context, bool delivered, bool cancel)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, context) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var now = clock.UtcNow;
        await SeedAsync(scope.ServiceProvider, context, now);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(context);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(context);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, now));
        if (delivered) { await outbox.MarkDeliveredAsync(original.Id, now); }
        else { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "stopped", now, original.RetryRevision)); }
        var beforeCapacity = (await reader.ReadAsync()).Value;
        Assert.Empty(await outbox.ReadPendingAsync(10, now));
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? attempt = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            foreach (var operation in new[] { "read", "deliver", "failure", "dead-letter", "cleanup" })
            {
                await using var cancellation = new TimedCallerCancellation();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                attempt = Task.Run(async () =>
                {
                    if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                    started.SetResult();
                    // An improperly accepted confirmation of a dead letter would be immediately eligible for cleanup.
                    await PerformAsync(outbox, cleanup, original, operation, now.AddDays(-8), cancellation.Token);
                });
                await started.Task;
                if (cancel)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else { await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2))); }
            }
        }
        finally
        {
            Assert.Equal(0, await holder.ReleaseAsync());
            if (attempt is not null)
            {
                try { await attempt; }
                catch (Exception error) when (error is CommittedFactCapacityBusyException or OperationCanceledException) { }
            }
        }
        Assert.Empty(await outbox.ReadPendingAsync(10, now.AddDays(1)));
        Assert.Equal(beforeCapacity, (await reader.ReadAsync()).Value);
        Assert.False(await outbox.MarkFailedAsync(original.Id, "late-failure", now, original.RetryRevision));
        Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "late-dead-letter", now, original.RetryRevision));
        Assert.Equal(0, await cleanup.CleanupAsync());
        await outbox.MarkDeliveredAsync(original.Id, now.AddDays(-8));
        Assert.Equal(delivered ? 0 : 1, await cleanup.CleanupAsync());
        if (delivered)
        {
            // Duplicate confirmation cannot replace the recent first confirmation with an older timestamp.
            Assert.Equal(beforeCapacity, (await reader.ReadAsync()).Value);
        }
        else
        {
            // A genuine broker confirmation can recover a dead letter; only expired confirmed cleanup releases quota.
            Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedRecords);
            Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedPayloadBytes);
        }
    }

    public static TheoryData<string, string, bool> Cases
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var context in new[] { "platform", "identity", "files", "scheduling" })
            {
                foreach (var operation in new[] { "read", "deliver", "failure", "dead-letter", "cleanup" })
                {
                    cases.Add(context, operation, false);
                    cases.Add(context, operation, true);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MaintenanceRefusal_PreservesPendingFactAndCapacity_ThenSameOperationRecovers(string context, string operation, bool cancel)
    {
        using var clock = new PausingClock(DateTimeOffset.UtcNow);
        await using var app = new MemoryBudgetApp(clock, context) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var now = clock.UtcNow;
        await SeedAsync(scope.ServiceProvider, context, now);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var reader = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>(context);
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(context);
        var before = Assert.Single(await outbox.ReadPendingAsync(10, now));
        var beforeCapacity = (await reader.ReadAsync()).Value;
        await using var cancellation = new TimedCallerCancellation();
        var holder = new PausedFactCleanup(clock, cleanup);
        Task? attempt = null;
        try
        {
            await holder.WaitUntilPausedAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            attempt = Task.Run(async () =>
            {
                if (cancel) { cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)); }
                started.SetResult();
                await PerformAsync(outbox, cleanup, before, operation, now, cancellation.Token);
            });
            await started.Task;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else { await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2))); }
            Assert.Equal("audit_capacity.unavailable", (await reader.ReadAsync()).Error.Code);
        }
        finally
        {
            Assert.Equal(0, await holder.ReleaseAsync());
            if (attempt is not null)
            {
                try { await attempt; }
                catch (Exception error) when (error is CommittedFactCapacityBusyException or OperationCanceledException) { }
            }
        }
        Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now)));
        Assert.Equal(beforeCapacity, (await reader.ReadAsync()).Value);

        switch (operation)
        {
            case "read": Assert.Equal(before, Assert.Single(await outbox.ReadPendingAsync(10, now))); break;
            case "deliver":
                await outbox.MarkDeliveredAsync(before.Id, now.AddDays(-8));
                Assert.Empty(await outbox.ReadPendingAsync(10, now));
                break;
            case "failure":
                Assert.True(await outbox.MarkFailedAsync(before.Id, "safe-test-failure", now.AddMinutes(1), before.RetryRevision));
                Assert.Empty(await outbox.ReadPendingAsync(10, now));
                var retried = Assert.Single(await outbox.ReadPendingAsync(10, now.AddMinutes(1)));
                Assert.Equal(before.Id, retried.Id);
                Assert.Equal(1, retried.AttemptCount);
                Assert.Equal(before.RetryRevision, retried.RetryRevision);
                break;
            case "dead-letter":
                Assert.True(await outbox.MarkDeadLetteredAsync(before.Id, "safe-test-failure", now, before.RetryRevision));
                Assert.Empty(await outbox.ReadPendingAsync(10, now));
                Assert.False(await outbox.MarkFailedAsync(before.Id, "must-not-reopen", now, before.RetryRevision));
                break;
            case "cleanup": Assert.Equal(0, await cleanup.CleanupAsync()); break;
        }
        Assert.Equal(beforeCapacity, (await reader.ReadAsync()).Value);
        if (operation == "dead-letter") { Assert.Equal(0, await cleanup.CleanupAsync()); }
        else
        {
            if (operation != "deliver") { await outbox.MarkDeliveredAsync(before.Id, now.AddDays(-8)); }
            Assert.Equal(1, await cleanup.CleanupAsync());
            Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedRecords);
            Assert.Equal(0, (await reader.ReadAsync()).Value.RetainedPayloadBytes);
        }
    }

    private static async Task PerformAsync(IOutboxStore outbox, ICommittedFactCleanup cleanup, OutboxEntry fact,
        string operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "read": await outbox.ReadPendingAsync(10, now, cancellationToken); break;
            case "deliver": await outbox.MarkDeliveredAsync(fact.Id, now, cancellationToken); break;
            case "failure": await outbox.MarkFailedAsync(fact.Id, "must-not-save", now.AddMinutes(1), fact.RetryRevision, cancellationToken); break;
            case "dead-letter": await outbox.MarkDeadLetteredAsync(fact.Id, "must-not-save", now, fact.RetryRevision, cancellationToken); break;
            case "cleanup": await cleanup.CleanupAsync(cancellationToken); break;
            default: throw new InvalidOperationException("Unknown maintenance test operation.");
        }
    }

    private static async Task SeedAsync(IServiceProvider services, string context, DateTimeOffset now)
    {
        switch (context)
        {
            case "platform": Assert.True((await services.GetRequiredService<SettingStore>().WriteAsync(SettingKey.Create("budget.maintenance").Value, "value")).IsSuccess); break;
            case "identity": Assert.True((await services.GetRequiredService<ISender>().SendAsync(new CreateRoleCommand("maintenance-budget", "Budget"))).IsSuccess); break;
            case "files":
                var file = StoredFile.Register(new StoredFileId(97431), FileName.Create("maintenance.bin").Value, "application/octet-stream", "owner", now).Value;
                await services.GetRequiredService<IStoredFileRepository>().SaveAsync(file);
                break;
            case "scheduling":
                var plan = ScheduledTask.Create(new ScheduledTaskId(97432), TaskCode.Create("maintenance-budget").Value, TimeSpan.FromHours(1), now,
                    ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
                Assert.True((await services.GetRequiredService<IScheduledTaskStore>().AddAsync(plan)).IsSuccess);
                break;
            default: throw new InvalidOperationException("Unknown maintenance test context.");
        }
    }
}
