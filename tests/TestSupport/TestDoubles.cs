using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.TestSupport;

/// <summary>
/// 单调递增的标识生成器。
/// <para>测试里要的是**可预测**，不是雪花算法——断言里出现一串 98328201679753216 毫无帮助。</para>
/// </summary>
/// <param name="start">起始值；第一个 <see cref="NextId"/> 返回它加一。</param>
public sealed class SequentialIdGenerator(long start = 1000) : IIdGenerator
{
    private long _next = start;

    /// <inheritdoc />
    public long NextId() => ++_next;
}

/// <summary>
/// 固定时钟：每一次读取都返回同一个时刻。
/// <para>领域层不依赖 <c>IClock</c>（时间由调用方传入），会用到它的是应用层服务与测试。</para>
/// </summary>
/// <param name="now">要固定的时刻。</param>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => now;
}

/// <summary>
/// 可变时钟：可以往前拨。
/// <para>用于"过了一段时间之后"的场景——例如停用一小时再启用，验证它不补跑。</para>
/// </summary>
/// <param name="now">初始时刻。</param>
public sealed class MutableClock(DateTimeOffset now) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = now;

    /// <summary>把时钟往前拨一段。</summary>
    /// <param name="by">推进的时长。</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}
