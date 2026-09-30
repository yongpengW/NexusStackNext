using NexusStackNext.BuildingBlocks.Infrastructure.Ids;

using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>雪花 ID 生成器：唯一、单调、WorkerId 生效，异常情况显式抛错。</summary>
public sealed class SnowflakeIdGeneratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static SnowflakeIdGenerator Create(int workerId, MutableClock clock) =>
        new(new IdGeneratorOptions { WorkerId = workerId }, clock);

    [Fact]
    public void NextId_IsStrictlyIncreasing_WhenClockIsFrozen()
    {
        var clock = new MutableClock(Start);
        var generator = Create(1, clock);

        var ids = Enumerable.Range(0, 100).Select(_ => generator.NextId()).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] > ids[i - 1], $"第 {i} 个 ID 没有递增。");
        }
    }

    [Fact]
    public void NextId_IsUnique_AcrossClockAdvances()
    {
        var clock = new MutableClock(Start);
        var generator = Create(7, clock);

        var ids = new List<long>();
        for (var round = 0; round < 50; round++)
        {
            ids.AddRange(Enumerable.Range(0, 40).Select(_ => generator.NextId()));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NextId_EncodesWorkerId()
    {
        var clock = new MutableClock(Start);
        var id = Create(513, clock).NextId();

        Assert.Equal(513, (int)((id >> 12) & 0x3FF));
    }

    [Fact]
    public void NextId_DiffersBetweenWorkers_AtTheSameInstant()
    {
        var clock = new MutableClock(Start);
        var a = Create(1, clock).NextId();
        var b = Create(2, clock).NextId();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NextId_ResetsSequence_AfterClockAdvances()
    {
        var clock = new MutableClock(Start);
        var generator = Create(1, clock);

        var first = generator.NextId();
        generator.NextId();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var afterAdvance = generator.NextId();

        Assert.True(afterAdvance > first);

        // 新的一毫秒从序列号 0 开始：低 12 位应为 0。
        Assert.Equal(0, (int)(afterAdvance & 0xFFF));
    }

    [Fact]
    public void NextId_WhenClockGoesBackwards_Throws()
    {
        // 回拨时若照常递增就会生成重复主键，且要到写库才暴露。宁可立刻失败。
        var clock = new MutableClock(Start);
        var generator = Create(1, clock);
        generator.NextId();

        clock.UtcNow = Start.AddMilliseconds(-5);

        var exception = Assert.Throws<InvalidOperationException>(() => generator.NextId());
        Assert.Contains("时钟回拨", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextId_WhenSequenceExhaustedInSameMillisecond_Throws()
    {
        var clock = new MutableClock(Start);
        var generator = Create(1, clock);

        // 4096 个可用（序列号 0..4095）。
        for (var i = 0; i < 4096; i++)
        {
            generator.NextId();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => generator.NextId());
        Assert.Contains("序列号耗尽", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsWorkerIdOutOfRange()
    {
        var clock = new MutableClock(Start);

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(-1, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(IdGeneratorOptions.MaxWorkerId + 1, clock));
    }

    [Fact]
    public void Constructor_AcceptsBoundaryWorkerIds()
    {
        var clock = new MutableClock(Start);

        Assert.Equal(0, (int)(Create(0, clock).NextId() >> 12 & 0x3FF));
        Assert.Equal(
            IdGeneratorOptions.MaxWorkerId,
            (int)(Create(IdGeneratorOptions.MaxWorkerId, clock).NextId() >> 12 & 0x3FF));
    }
}
