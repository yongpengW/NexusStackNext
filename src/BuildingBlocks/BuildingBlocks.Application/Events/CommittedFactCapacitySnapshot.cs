namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>所属上下文的有限事实容量；确认未清理的事实仍在保留占用内。</summary>
/// <param name="Context">代码声明的所属上下文。</param>
/// <param name="IsPersistent">是否由持久数据库保留；false 表示仅当前进程生命周期。</param>
/// <param name="MaxRecords">最大保留条数。</param>
/// <param name="MaxPayloadBytes">最大 UTF-8 正文总字节。</param>
/// <param name="MaxRecordPayloadBytes">单条正文的 UTF-8 字节上限。</param>
/// <param name="RetainedRecords">已保留事实条数，包含待投递、死信和确认副本。</param>
/// <param name="RetainedPayloadBytes">已保留事实正文的 UTF-8 总字节。</param>
public sealed record CommittedFactCapacitySnapshot(string Context, bool IsPersistent, long MaxRecords,
    long MaxPayloadBytes, int MaxRecordPayloadBytes, long RetainedRecords, long RetainedPayloadBytes)
{
    /// <summary>条数剩余额度；历史占用超过策略时为零。</summary>
    public long RemainingRecords => Math.Max(0, MaxRecords - RetainedRecords);
    /// <summary>正文剩余字节；历史占用超过策略时为零。</summary>
    public long RemainingPayloadBytes => Math.Max(0, MaxPayloadBytes - RetainedPayloadBytes);
    /// <summary>历史保留总量已超过当前条数或总字节策略；不扫描正文推断单条状态。</summary>
    public bool OverLimit => RetainedRecords > MaxRecords || RetainedPayloadBytes > MaxPayloadBytes;
}
