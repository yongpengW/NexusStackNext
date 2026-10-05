namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>开发恢复凭据池的启动上限；只能缩小默认额度，不覆盖 PostgreSQL 持久额度。</summary>
public sealed record MemoryFactDeliveryRecoveryControlOptions
{
    /// <summary>恢复请求数，默认一千，允许一至一千。</summary>
    public long MaxRecords { get; init; } = 1000;
    /// <summary>恢复凭据 UTF-8 总字节，默认16MiB，允许正数且不超过默认值。</summary>
    public long MaxPayloadBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>单条恢复凭据 UTF-8 字节，默认16KiB，不得超过总量与默认值。</summary>
    public int MaxRecordPayloadBytes { get; init; } = 16 * 1024;

    /// <summary>在启动时拒绝非正数、不一致或扩大默认上限的配置。</summary>
    public void Validate()
    {
        if (MaxRecords is <= 0 or > 1000 || MaxPayloadBytes <= 0 || MaxPayloadBytes > 16 * 1024 * 1024
            || MaxRecordPayloadBytes <= 0 || MaxRecordPayloadBytes > 16 * 1024 || MaxRecordPayloadBytes > MaxPayloadBytes)
        { throw new InvalidOperationException("内存恢复凭据额度只能在默认上限内缩小，且单条不得超过总量。"); }
    }
}
