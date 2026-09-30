using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Domain.Tests;

/// <summary>Scheduling 聚合的版本号（ADR-0011）。</summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static ScheduledTask NewTask() => ScheduledTask.Create(
        new ScheduledTaskId(1),
        TaskCode.Create("demo.tick").Value,
        TimeSpan.FromSeconds(5),
        Now).Value;

    [Fact]
    public void ExecutionAndIntervalChange_Bump()
    {
        var task = NewTask();
        Assert.Equal(1, task.Version);

        Assert.True(task.MarkExecuted(Now).IsSuccess);
        Assert.Equal(2, task.Version);

        // 相同的间隔不是改变。
        Assert.True(task.ChangeInterval(TimeSpan.FromSeconds(5)).IsSuccess);
        Assert.Equal(2, task.Version);

        Assert.True(task.ChangeInterval(TimeSpan.FromSeconds(9)).IsSuccess);
        Assert.Equal(3, task.Version);
    }

    [Fact]
    public void DisableThenEnable_BumpsOnceEach_RepeatsDoNot()
    {
        var task = NewTask();

        task.Disable();
        Assert.Equal(2, task.Version);

        task.Disable();
        Assert.Equal(2, task.Version);

        task.Enable(Now);
        Assert.Equal(3, task.Version);
    }
}
