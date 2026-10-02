using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Scheduling.Application.Tests;

/// <summary>Scheduling：任务到期判定与"每次执行都推进下次时刻"。</summary>
public sealed class SchedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static TaskCode Code(string value = "scheduling.heartbeat") => TaskCode.Create(value).Value;

    private static ScheduleTarget Target => ScheduleTarget.Create("costing.recalculate", Guid.Parse("44444444-4444-4444-4444-444444444444")).Value;

    private static ScheduledTask NewTask(TimeSpan? interval = null, DateTimeOffset? firstRun = null) =>
        ScheduledTask.Create(
            new ScheduledTaskId(1),
            Code(),
            interval ?? TimeSpan.FromMinutes(1),
            firstRun ?? Now, Target, "42").Value;

    [Fact]
    public void Create_RejectsNonPositiveInterval()
    {
        Assert.True(ScheduledTask.Create(new ScheduledTaskId(1), Code(), TimeSpan.Zero, Now, Target, "42").IsFailure);
        Assert.True(ScheduledTask.Create(new ScheduledTaskId(1), Code(), TimeSpan.FromSeconds(-1), Now, Target, "42").IsFailure);
    }

    [Theory]
    [InlineData("Scheduling.Heartbeat", "scheduling.heartbeat")]
    [InlineData("  a.b  ", "a.b")]
    public void TaskCode_Normalizes(string input, string expected)
    {
        Assert.Equal(expected, TaskCode.Create(input).Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void TaskCode_RejectsMalformed(string? input)
    {
        Assert.True(TaskCode.Create(input).IsFailure);
    }

    [Fact]
    public void IsDue_IsFalseBeforeFirstRun_AndTrueAtIt()
    {
        var task = NewTask(firstRun: Now + TimeSpan.FromMinutes(1));

        Assert.False(task.IsDue(Now));
        Assert.True(task.IsDue(Now + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Disable_ClearsNextRunAt_SoItIsNoLongerDue()
    {
        // 停用任务若不清空下次时刻，就会永远被判为到期——这正是"空转"的一种形态。
        var task = NewTask(firstRun: Now);
        Assert.True(task.IsDue(Now));

        task.Disable();

        Assert.False(task.IsDue(Now));
        Assert.Null(task.NextRunAt);
    }

    [Fact]
    public void MarkTriggered_AdvancesNextRunAtFromTheActualRunTime_NotTheSchedule()
    {
        // 迟到不补跑：下次时刻从实际执行时刻起算，否则停机一小时后重启会瞬间涌入几十次补跑。
        var task = NewTask(interval: TimeSpan.FromMinutes(10), firstRun: Now);
        var late = Now + TimeSpan.FromHours(1);

        Assert.True(task.MarkTriggered(late).IsSuccess);

        Assert.Equal(late, task.LastRunAt);
        Assert.Equal(late + TimeSpan.FromMinutes(10), task.NextRunAt);
    }

    [Fact]
    public void MarkTriggered_RaisesEvent_ButDisabledTaskRefuses()
    {
        var task = NewTask(firstRun: Now);
        task.ClearDomainEvents();

        Assert.True(task.MarkTriggered(Now).IsSuccess);
        Assert.IsType<ScheduledTaskTriggered>(Assert.Single(task.DomainEvents));

        task.Disable();
        Assert.True(task.MarkTriggered(Now).IsFailure);
    }

    [Fact]
    public async Task ScheduleRunner_TriggersOnlyDueTasks_AndLeavesARecord()
    {
        // 参照仓库的 PlanTaskService 是空壳：永远静默跳过，还以 1Hz 空转。
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        var due = NewTask(firstRun: Now);
        var notYet = ScheduledTask.Create(new ScheduledTaskId(2), Code("scheduling.later"), TimeSpan.FromMinutes(1), Now + TimeSpan.FromHours(1), Target, "42").Value;
        Assert.True((await store.AddAsync(due)).IsSuccess);
        Assert.True((await store.AddAsync(notYet)).IsSuccess);

        var runner = new ScheduleRunner(store, clock);
        var result = await runner.RunOnceAsync();

        Assert.Equal(new ScheduleRunResult(1, 1, 0), result);
        var saved = Assert.IsType<ScheduledTask>(await store.FindAsync(due.Id));
        Assert.Equal(Now, saved.LastRunAt);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), saved.NextRunAt);
        var occurrence = Assert.Single((await store.ReadOccurrencesAsync(due.Id.Value, 0, 100)).Items);
        Assert.Equal(1, occurrence.TriggerSequence);
        Assert.Equal("Pending", occurrence.DeliveryState);
        Assert.Equal(occurrence.OccurrenceId, Assert.Single(await store.ReadPendingAsync(10, Now)).Id);
        Assert.Empty((await store.ReadOccurrencesAsync(notYet.Id.Value, 0, 100)).Items);
    }

    [Fact]
    public async Task ScheduleRunner_SecondRunAtSameInstant_IsNoOp()
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        Assert.True((await store.AddAsync(NewTask(firstRun: Now))).IsSuccess);
        var runner = new ScheduleRunner(store, clock);

        var first = await runner.RunOnceAsync();
        var second = await runner.RunOnceAsync();

        Assert.Equal(1, first.Triggered);
        Assert.Equal(0, second.Triggered);
    }

    [Fact]
    public async Task ScheduleRunner_TriggersAgainAfterIntervalElapses()
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        Assert.True((await store.AddAsync(NewTask(interval: TimeSpan.FromMinutes(1), firstRun: Now))).IsSuccess);
        var runner = new ScheduleRunner(store, clock);

        await runner.RunOnceAsync();
        clock.UtcNow = Now + TimeSpan.FromMinutes(1);

        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
    }
}
