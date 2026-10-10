namespace NexusStackNext.Auditing.Application;

/// <summary>中央事实与观察记录的容量调查；只读所属审计存储。</summary>
public interface IAuditStorageCapacityReader
{
    /// <summary>读取有限等待的容量快照；故障不会伪装成空库。</summary>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>两类记录的当前用量与本实例接纳上限。</returns>
    Task<AuditStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>有限记录额度，不代表数据库磁盘字节配额。</summary>
/// <param name="Records">当前记录数，包含调查默认窗口之外的记录。</param>
/// <param name="InstanceLimit">本实例配置的接纳上限。</param>
public sealed record AuditStoragePoolCapacity(long Records, long InstanceLimit)
{
    /// <summary>尚可接纳的记录数；降低配置后已有记录仍保留。</summary>
    public long Available => Math.Max(0, InstanceLimit - Records);
}

/// <summary>中央容量调查；操作的开始与结束观察分别占一条。</summary>
/// <param name="Facts">已提交事实。</param>
/// <param name="Observations">操作观察。</param>
public sealed record AuditStorageCapacitySnapshot(AuditStoragePoolCapacity Facts, AuditStoragePoolCapacity Observations);

/// <summary>接纳暂时不可用；消息消费者应保留原消息，不能视为永久无效消息。</summary>
public sealed class AuditStorageUnavailableException : Exception
{
    /// <summary>构造不含消息正文或数据库诊断的故障。</summary>
    /// <param name="capacityExhausted">额度已满；否则为短等待超时。</param>
    public AuditStorageUnavailableException(bool capacityExhausted)
        : base(capacityExhausted ? "中央审计记录容量不足，请恢复容量后重试。" : "中央审计存储等待超时，请稍后重试。")
        => CapacityExhausted = capacityExhausted;

    /// <summary>是否因额度已满拒绝新记录。</summary>
    public bool CapacityExhausted { get; }
}
