namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>每个上下文显式装配的容量诊断预算，不改变业务写入的重试策略。</summary>
public sealed record CommittedFactCapacityReadOptions
{
    /// <summary>连接及查询共用的取消预算，默认三秒，允许五十毫秒至三十秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>非法预算在装配时失败。</summary>
    public void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("事实容量诊断预算超出允许范围。");
        }
    }
}
