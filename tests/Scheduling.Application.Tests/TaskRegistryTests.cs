using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Scheduling.Application.Tests;

/// <summary>
/// 任务注册表：定义、查重、停用、启用。
/// <para>
/// 重点是**停用必须清空下次计划时刻**。只把 <c>IsEnabled</c> 置 false 而留着
/// <c>NextRunAt</c>，任务在存储眼里仍然是"到期"的——于是它每轮都被读出来、每轮都被跳过。
/// 日志上看起来一切正常，**实际上调度器在空转**。
/// </para>
/// </summary>
public sealed class TaskRegistryTests
{
    [Fact]
    public async Task PausedRuleChange_PreservesPause_AndResumeUsesTheNewCalendar()
    {
        var (registry, store, clock) = NewRegistry();
        var created = await registry.DefineAsync(Code("paused-calendar"), TimeSpan.FromSeconds(5), Target, "42");
        Assert.True(created.IsSuccess);
        var id = created.Value.Id;
        Assert.True((await registry.PauseAsync(id, 1)).IsSuccess);
        var monthly = new ScheduleRuleInput("MonthlyDay", TimeZoneId: "Asia/Shanghai", Day: 31, Hour: 9, Minute: 0);
        Assert.True((await registry.UpdateRuleAsync(id, 2, monthly)).IsSuccess);
        var changed = Assert.IsType<ScheduledTask>(await registry.FindAsync(id));
        Assert.False(changed.IsEnabled);
        Assert.Null(changed.NextRunAt);
        Assert.Equal(3, changed.Version);
        Assert.Equal(2, changed.ScheduleRevision);
        Assert.Empty(await store.ReadDueAsync(Now.AddYears(1), 50));
        Assert.True((await registry.UpdateRuleAsync(id, 3, monthly)).IsSuccess);
        Assert.Equal(3, (await registry.FindAsync(id))!.Version);
        Assert.Equal(TaskRegistry.Conflict, (await registry.UpdateRuleAsync(id, 2, monthly)).Error);
        clock.UtcNow = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True((await registry.ResumeAsync(id, 3)).IsSuccess);
        var resumed = Assert.IsType<ScheduledTask>(await registry.FindAsync(id));
        Assert.True(resumed.IsEnabled);
        Assert.Equal(new DateTimeOffset(2027, 2, 28, 1, 0, 0, TimeSpan.Zero), resumed.NextRunAt);
        Assert.Equal(4, resumed.Version);
        Assert.Equal(2, resumed.ScheduleRevision);
        Assert.Empty((await store.ReadOccurrencesAsync(id.Value, 0, 100)).Items);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static (TaskRegistry Registry, InMemoryScheduledTaskStore Store, MutableClock Clock) NewRegistry()
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        return (new TaskRegistry(store, new SequentialIdGenerator(9000), clock, new CronScheduleCalendar()), store, clock);
    }

    private static ScheduleTarget Target => ScheduleTarget.Create("costing.recalculate", Guid.Parse("44444444-4444-4444-4444-444444444444")).Value;

    private static TaskCode Code(string value) => TaskCode.Create(value).Value;
    [Fact]
    public async Task Define_SavesTheTaskAndUsesNowAsTheDefaultFirstRun()
    {
        var (registry, _, _) = NewRegistry();

        var defined = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(5), Target, "42");

        Assert.True(defined.IsSuccess);
        Assert.Equal("demo.tick", defined.Value.Code.Value);
        Assert.Equal(Now, defined.Value.NextRunAt);
        Assert.True(defined.Value.IsEnabled);
        Assert.Single(await registry.ListAsync());
    }

    [Fact]
    public async Task Define_NormalizesTheCode()
    {
        var (registry, _, _) = NewRegistry();

        var defined = await registry.DefineAsync(Code("  DEMO.Tick  "), TimeSpan.FromSeconds(5), Target, "42");

        Assert.Equal("demo.tick", defined.Value.Code.Value);
    }

    [Fact]
    public async Task Define_RejectsADuplicateCode()
    {
        var (registry, _, _) = NewRegistry();
        await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(5), Target, "42");

        var again = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(9), Target, "42");

        Assert.True(again.IsFailure);
        Assert.Equal("scheduling.task_code.taken", again.Error.Code);
        Assert.Single(await registry.ListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Define_RejectsANonPositiveInterval(int seconds)
    {
        var (registry, _, _) = NewRegistry();

        var defined = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(seconds), Target, "42");

        Assert.True(defined.IsFailure);
        Assert.Equal("scheduling.interval.invalid", defined.Error.Code);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Pause_ClearsTheNextRunSoTheTaskStopsBeingDue()
    {
        // 这条是整组里最重要的一条：留下 NextRunAt 会让任务每轮都被读出来、每轮都被跳过。
        var (registry, store, _) = NewRegistry();
        var defined = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(5), Target, "42");

        var paused = await registry.PauseAsync(defined.Value.Id, defined.Value.Version);

        Assert.True(paused.IsSuccess);

        var reloaded = Assert.Single(await registry.ListAsync());
        Assert.False(reloaded.IsEnabled);
        Assert.Null(reloaded.NextRunAt);

        // 从存储的角度确认：它不再出现在"到期"里。
        Assert.Empty(await store.ReadDueAsync(Now + TimeSpan.FromHours(1), 10));
    }

    [Fact]
    public async Task Resume_RestartsFromNowInsteadOfCatchingUp()
    {
        // 停用一小时后再启用，不该补跑几十次。
        var (registry, _, clock) = NewRegistry();
        var defined = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(30), Target, "42");
        await registry.PauseAsync(defined.Value.Id, defined.Value.Version);

        clock.UtcNow = Now + TimeSpan.FromHours(1);
        var resumed = await registry.ResumeAsync(defined.Value.Id, 2);

        Assert.True(resumed.IsSuccess);

        var reloaded = Assert.Single(await registry.ListAsync());
        Assert.True(reloaded.IsEnabled);
        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(30), reloaded.NextRunAt);
    }

    [Fact]
    public async Task PauseAndResume_UnknownTask_Fail()
    {
        var (registry, _, _) = NewRegistry();

        var paused = await registry.PauseAsync(new ScheduledTaskId(404), 1);
        var resumed = await registry.ResumeAsync(new ScheduledTaskId(404), 1);

        Assert.Equal("scheduling.task.not_found", paused.Error.Code);
        Assert.Equal("scheduling.task.not_found", resumed.Error.Code);
    }

    [Fact]
    public async Task Find_ReturnsTheTaskOrNull()
    {
        var (registry, _, _) = NewRegistry();
        var defined = await registry.DefineAsync(Code("demo.tick"), TimeSpan.FromSeconds(5), Target, "42");

        Assert.NotNull(await registry.FindAsync(defined.Value.Id));
        Assert.Null(await registry.FindAsync(new ScheduledTaskId(404)));
    }

    [Fact]
    public async Task List_IsSortedByCode()
    {
        var (registry, _, _) = NewRegistry();
        await registry.DefineAsync(Code("c.task"), TimeSpan.FromSeconds(5), Target, "42");
        await registry.DefineAsync(Code("a.task"), TimeSpan.FromSeconds(5), Target, "42");
        await registry.DefineAsync(Code("b.task"), TimeSpan.FromSeconds(5), Target, "42");

        var all = await registry.ListAsync();

        Assert.Equal(["a.task", "b.task", "c.task"], all.Select(static task => task.Code.Value));
    }
}
