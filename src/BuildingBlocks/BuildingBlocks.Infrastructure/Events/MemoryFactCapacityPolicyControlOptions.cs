namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>Memory 所属控制池的启动上限，只能缩小默认有限额度；不覆盖 PostgreSQL 持久策略。</summary>
public sealed record MemoryFactCapacityPolicyControlOptions
{
    /// <summary>请求凭据条数，默认一千，允许一至一千。</summary>
    public long MaxRecords { get; init; } = 1000;
    /// <summary>凭据及控制事实的 UTF-8 总字节，默认16MiB，只允许正数且不超过默认值。</summary>
    public long MaxPayloadBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>单个请求的 UTF-8 字节，默认16KiB，只允许正数且不超过总量和默认值。</summary>
    public int MaxRecordPayloadBytes { get; init; } = 16 * 1024;

    /// <summary>拒绝非正数、不一致及扩大控制池默认上限的启动配置。</summary>
    public void Validate()
    {
        if (MaxRecords is <= 0 or > 1000 || MaxPayloadBytes <= 0 || MaxPayloadBytes > 16 * 1024 * 1024
            || MaxRecordPayloadBytes <= 0 || MaxRecordPayloadBytes > 16 * 1024 || MaxRecordPayloadBytes > MaxPayloadBytes)
        {
            throw new InvalidOperationException("内存策略控制额度只能在默认上限内缩小，且单条不得超过总量。");
        }
    }
}
