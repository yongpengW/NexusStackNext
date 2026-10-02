using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Scheduling.Application.Tests;

/// <summary>Scheduling：任务到期判定与"每次执行都推进下次时刻"。</summary>
public sealed class SchedulingTests
{
    [Theory]
    [InlineData("FireOnce", 29999999, "Triggered", 1, 31)]
    [InlineData("Skip", 29999999, "Triggered", 1, 31)]
    [InlineData("FireOnce", 30000000, "Triggered", 1, 32)]
    [InlineData("Skip", 30000000, "Triggered", 1, 32)]
    [InlineData("FireOnce", 30000001, "Coalesced", 1, 32)]
    [InlineData("Skip", 30000001, "Skipped", 0, 32)]
    public async Task CalendarGrace_IncludesItsBoundary_AndDecidesTheWindowOnce(string policy, long lateMicroseconds,
        string expectedKind, int expectedTriggers, int nextSecond)
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        var calendar = new CronScheduleCalendar();
        var registry = new TaskRegistry(store, new SequentialIdGenerator(), clock, calendar);
        var created = await registry.DefineAsync(Code("grace"), new ScheduleRuleInput("Cron", "* * * * * *", "UTC", MisfirePolicy: policy), Target, "42");
        Assert.True(created.IsSuccess);
        clock.UtcNow = Now.AddSeconds(1).AddTicks(lateMicroseconds * 10);
        var runner = new ScheduleRunner(store, clock, calendar);
        var result = await runner.RunOnceAsync();
        Assert.Equal(expectedTriggers, result.Triggered);
        Assert.Empty(result.FailedPlanIds);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        var decision = Assert.Single((await store.ReadDecisionsAsync(created.Value.Id.Value, 0, 100)).Items);
        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(Now.AddSeconds(1), decision.ScheduledAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 12, 0, nextSecond, TimeSpan.Zero), decision.NextRunAt);
        Assert.Equal(expectedTriggers, (await store.ReadOccurrencesAsync(created.Value.Id.Value, 0, 100)).Items.Count);
    }

    [Theory]
    [InlineData("FireOnce", 1, "Coalesced")]
    [InlineData("Skip", 0, "Skipped")]
    public async Task SecondRule_AfterTwoYearsOffline_ProducesOnlyOneDecision(string policy, int triggers, string kind)
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        var calendar = new CronScheduleCalendar();
        var registry = new TaskRegistry(store, new SequentialIdGenerator(), clock, calendar);
        var created = await registry.DefineAsync(Code("long-outage"), new ScheduleRuleInput("Cron", "* * * * * *", "UTC", MisfirePolicy: policy), Target, "42");
        Assert.True(created.IsSuccess);
        clock.UtcNow = new DateTimeOffset(2028, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var result = await new ScheduleRunner(store, clock, calendar).RunOnceAsync();
        Assert.Equal(triggers, result.Triggered);
        Assert.Empty(result.FailedPlanIds);
        var decision = Assert.Single((await store.ReadDecisionsAsync(created.Value.Id.Value, 0, 100)).Items);
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 12, 0, 1, TimeSpan.Zero), decision.ScheduledAt);
        Assert.Equal(new DateTimeOffset(2028, 9, 29, 12, 0, 1, TimeSpan.Zero), decision.NextRunAt);
        Assert.Equal(triggers, (await store.ReadOccurrencesAsync(created.Value.Id.Value, 0, 100)).Items.Count);
    }

    [Fact]
    public async Task FailedCalendarBatch_IsDeferred_SoLaterHealthyPlansCanRun()
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        var calendar = new CronScheduleCalendar();
        // 布置部署后时区数据不可用的已有规则；客户端创建仍由真实计算适配器验证。
        for (var id = 1; id <= 50; id++)
        {
            var rule = new ScheduleRule("Cron", "* * * * *", "Unknown/RemovedZone", 5, MisfirePolicy: "FireOnce", GraceSeconds: 30);
            var failed = ScheduledTask.Create(new ScheduledTaskId(id), Code($"failed-{id}"), rule, Now.AddMinutes(-1), Target, "42").Value;
            Assert.True((await store.AddAsync(failed)).IsSuccess);
        }
        Assert.True((await store.AddAsync(ScheduledTask.Create(new ScheduledTaskId(51), Code("healthy"), TimeSpan.FromMinutes(1), Now, Target, "42").Value)).IsSuccess);
        var runner = new ScheduleRunner(store, clock, calendar);
        var first = await runner.RunOnceAsync();
        Assert.Equal(50, first.Examined);
        Assert.Equal(50, first.FailedPlanIds.Count);
        var deferred = Assert.IsType<ScheduledTask>(await store.FindAsync(new ScheduledTaskId(1)));
        Assert.Equal(Now.AddMinutes(-1), deferred.NextRunAt);
        Assert.Equal(Now.AddMinutes(1), deferred.RetryAt);
        Assert.Equal(1, deferred.SchedulingFailureCount);
        Assert.Equal(2, deferred.Version);
        Assert.Equal(1, deferred.ScheduleRevision);
        Assert.Null(deferred.LastRunAt);
        Assert.Equal(0, deferred.TriggerSequence);
        Assert.Empty((await store.ReadDecisionsAsync(1, 0, 100)).Items);
        var next = await runner.RunOnceAsync();
        Assert.Equal(1, next.Examined);
        Assert.Equal(1, next.Triggered);
        Assert.Empty(next.FailedPlanIds);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
    }

    [Fact]
    public async Task RepeatedFailure_IsBounded_AndChangingRuleRecoversWithoutRewritingHistory()
    {
        var store = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer());
        var clock = new MutableClock(Now);
        var rule = new ScheduleRule("Cron", "* * * * *", "Unknown/RemovedZone", 5, MisfirePolicy: "FireOnce", GraceSeconds: 30);
        var plan = ScheduledTask.Create(new ScheduledTaskId(1), Code(), rule, Now, Target, "42").Value;
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());
        var delays = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };
        for (var index = 0; index < delays.Length; index++)
        {
            Assert.Equal(1, Assert.Single((await runner.RunOnceAsync()).FailedPlanIds));
            plan = Assert.IsType<ScheduledTask>(await store.FindAsync(plan.Id));
            Assert.Equal(clock.UtcNow.AddMinutes(delays[index]), plan.RetryAt);
            Assert.Equal(index + 1, plan.SchedulingFailureCount);
            Assert.Equal(Now, plan.NextRunAt);
            Assert.Equal(1, plan.ScheduleRevision);
            Assert.Empty((await store.ReadOccurrencesAsync(1, 0, 100)).Items);
            Assert.Empty((await store.ReadDecisionsAsync(1, 0, 100)).Items);
            clock.UtcNow = plan.RetryAt!.Value.AddTicks(-10);
            Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
            clock.UtcNow = clock.UtcNow.AddTicks(10);
        }
        var registry = new TaskRegistry(store, new SequentialIdGenerator(), clock, new CronScheduleCalendar());
        Assert.True((await registry.UpdateRuleAsync(plan.Id, plan.Version,
            new ScheduleRuleInput("Cron", "* * * * *", "UTC"))).IsSuccess);
        var repaired = Assert.IsType<ScheduledTask>(await store.FindAsync(plan.Id));
        Assert.Null(repaired.RetryAt);
        Assert.Null(repaired.LastSchedulingErrorCode);
        Assert.Equal(0, repaired.SchedulingFailureCount);
        Assert.Equal(2, repaired.ScheduleRevision);
        clock.UtcNow = repaired.NextRunAt!.Value;
        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
        Assert.Single((await store.ReadOccurrencesAsync(1, 0, 100)).Items);
        Assert.Equal(2, Assert.Single((await store.ReadDecisionsAsync(1, 0, 100)).Items).ScheduleRevision);
    }

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

        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());
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
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());

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
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());

        await runner.RunOnceAsync();
        clock.UtcNow = Now + TimeSpan.FromMinutes(1);

        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
    }
}
