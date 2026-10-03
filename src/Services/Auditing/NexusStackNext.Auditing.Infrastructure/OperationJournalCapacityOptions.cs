namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>来源日志已接受记录的容量；已交付和死信在清理前仍占用额度。</summary>
public sealed record OperationJournalCapacityOptions
{
    /// <summary>同一 journal 存储最多保留的记录数，默认十万条。</summary>
    public long MaxRecords { get; init; } = 100_000;

    /// <summary>恢复凭据的独立记录额度，默认一万条，不占普通观察额度。</summary>
    public int MaxRecoveryRecords { get; init; } = 10_000;

    /// <summary>所有保留消息的 UTF-8 载荷总字节数，默认 256 MiB；不等于数据库磁盘配额。</summary>
    public long MaxPayloadBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>单条消息的 UTF-8 载荷上限，默认 16 KiB。</summary>
    public int MaxRecordPayloadBytes { get; init; } = 16 * 1024;

    internal void Validate()
    {
        if (MaxRecords is < 1 or > 10_000_000)
        {
            throw new InvalidOperationException("OperationJournal:Capacity:MaxRecords 必须在 1 到 10000000 之间。");
        }
        if (MaxRecoveryRecords is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException("OperationJournal:Capacity:MaxRecoveryRecords 必须在 1 到 1000000 之间。");
        }
        if (MaxPayloadBytes is < 1 or > 68_719_476_736 || MaxRecordPayloadBytes is < 1 or > 65_536)
        {
            throw new InvalidOperationException("OperationJournal:Capacity 载荷总量必须在 1 字节到 64 GiB，单条载荷必须在 1 字节到 64 KiB 之间。");
        }
    }
}
