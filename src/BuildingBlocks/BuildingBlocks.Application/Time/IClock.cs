namespace NexusStackNext.BuildingBlocks.Application.Time;

/// <summary>
/// 可注入的时钟。
/// <para>
/// 参照仓库的 <c>CronScheduleService</c> 直接用静态时间，导致调度逻辑无法在测试里确定"现在几点"。
/// 任何需要"现在"的代码都必须从这里拿。
/// </para>
/// </summary>
public interface IClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>默认实现，返回系统时间。测试里应替换为固定时钟。</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
