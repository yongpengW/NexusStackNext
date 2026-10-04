namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>每个上下文独立装配的内存事实保留策略；默认值与 PostgreSQL 事实容量一致。</summary>
public sealed record MemoryCommittedFactCapacityOptions
{
    /// <summary>待投递、死信与尚未清理的确认副本条数上限。</summary>
    public long MaxRecords { get; init; } = 100000;
    /// <summary>保留事实正文的 UTF-8 总字节上限。</summary>
    public long MaxPayloadBytes { get; init; } = 268435456;
    /// <summary>单条事实正文的 UTF-8 字节上限。</summary>
    public int MaxRecordPayloadBytes { get; init; } = 16384;

    /// <summary>拒绝非正数和单条额度超过总额度的策略。</summary>
    public void Validate()
    {
        if (MaxRecords <= 0 || MaxPayloadBytes <= 0 || MaxRecordPayloadBytes <= 0 || MaxRecordPayloadBytes > MaxPayloadBytes)
        {
            throw new InvalidOperationException("内存事实容量策略超出允许范围。");
        }
    }
}
