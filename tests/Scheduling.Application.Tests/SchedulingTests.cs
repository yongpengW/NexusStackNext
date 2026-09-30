using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Scheduling.Application.Tests;

/// <summary>内存任务存储，实现真实语义：保存后状态可见。</summary>
internal sealed class FakeTaskStore : IScheduledTaskStore
{
    private readonly Dictionary<long, ScheduledTask> _tasks = [];

    public int SaveCount { get; private set; }

    public void Add(ScheduledTask task) => _tasks[task.Id.Value] = task;

    public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTask>>(
        [
            .. _tasks.Values.Where(task => task.IsDue(now)).OrderBy(static t => t.Id.Value).Take(batchSize),
        ]);

    public Task SaveAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        _tasks[task.Id.Value] = task;
        SaveCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTask>>(
        [
            .. _tasks.Values.OrderBy(static t => t.Id.Value),
        ]);
}

/// <summary>Scheduling：任务到期判定与"每次执行都推进下次时刻"。</summary>
public sealed class SchedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static TaskCode Code(string value = "scheduling.heartbeat") => TaskCode.Create(value).Value;

    private static ScheduledTask NewTask(TimeSpan? interval = null, DateTimeOffset? firstRun = null) =>
        ScheduledTask.Create(
            new ScheduledTaskId(1),
            Code(),
            interval ?? TimeSpan.FromMinutes(1),
            firstRun ?? Now).Value;

    [Fact]
    public void Create_RejectsNonPositiveInterval()
    {
        Assert.True(ScheduledTask.Create(new ScheduledTaskId(1), Code(), TimeSpan.Zero, Now).IsFailure);
        Assert.True(ScheduledTask.Create(new ScheduledTaskId(1), Code(), TimeSpan.FromSeconds(-1), Now).IsFailure);
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
    public void MarkExecuted_AdvancesNextRunAtFromTheActualRunTime_NotTheSchedule()
    {
        // 迟到不补跑：下次时刻从实际执行时刻起算，否则停机一小时后重启会瞬间涌入几十次补跑。
        var task = NewTask(interval: TimeSpan.FromMinutes(10), firstRun: Now);
        var late = Now + TimeSpan.FromHours(1);

        Assert.True(task.MarkExecuted(late).IsSuccess);

        Assert.Equal(late, task.LastRunAt);
        Assert.Equal(late + TimeSpan.FromMinutes(10), task.NextRunAt);
    }

    [Fact]
    public void MarkExecuted_RaisesEvent_ButDisabledTaskRefuses()
    {
        var task = NewTask(firstRun: Now);
        task.ClearDomainEvents();

        Assert.True(task.MarkExecuted(Now).IsSuccess);
        Assert.IsType<ScheduledTaskExecuted>(Assert.Single(task.DomainEvents));

        task.Disable();
        Assert.True(task.MarkExecuted(Now).IsFailure);
    }

    [Fact]
    public async Task ScheduleRunner_TriggersOnlyDueTasks_AndLeavesARecord()
    {
        // 参照仓库的 PlanTaskService 是空壳：永远静默跳过，还以 1Hz 空转。
        var store = new FakeTaskStore();
        var clock = new MutableClock(Now);
        var due = NewTask(firstRun: Now);
        var notYet = ScheduledTask.Create(new ScheduledTaskId(2), Code("scheduling.later"), TimeSpan.FromMinutes(1), Now + TimeSpan.FromHours(1)).Value;
        store.Add(due);
        store.Add(notYet);

        var runner = new ScheduleRunner(store, clock);
        var result = await runner.RunOnceAsync();

        Assert.Equal(new ScheduleRunResult(1, 1, 0), result);
        Assert.Equal(1, store.SaveCount);

        // 留下了记录：下次时刻已推进。
        Assert.Equal(Now, due.LastRunAt);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), due.NextRunAt);
    }

    [Fact]
    public async Task ScheduleRunner_SecondRunAtSameInstant_IsNoOp()
    {
        var store = new FakeTaskStore();
        var clock = new MutableClock(Now);
        store.Add(NewTask(firstRun: Now));
        var runner = new ScheduleRunner(store, clock);

        var first = await runner.RunOnceAsync();
        var second = await runner.RunOnceAsync();

        Assert.Equal(1, first.Triggered);
        Assert.Equal(0, second.Triggered);
    }

    [Fact]
    public async Task ScheduleRunner_TriggersAgainAfterIntervalElapses()
    {
        var store = new FakeTaskStore();
        var clock = new MutableClock(Now);
        store.Add(NewTask(interval: TimeSpan.FromMinutes(1), firstRun: Now));
        var runner = new ScheduleRunner(store, clock);

        await runner.RunOnceAsync();
        clock.UtcNow = Now + TimeSpan.FromMinutes(1);

        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
    }
}
