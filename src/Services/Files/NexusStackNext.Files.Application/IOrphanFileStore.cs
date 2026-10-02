namespace NexusStackNext.Files.Application;

/// <summary>能在写入保护下回收孤儿字节的存储能力。</summary>
public interface IOrphanFileStore
{
    /// <summary>有界扫描并回收旧的孤儿；必须在持有写入保护时调用退役函数，且只删除已确认退役的句柄。</summary>
    /// <param name="retireUnreferenced">原子确认没有引用并持久退役；数据库异常时不得删除。</param>
    /// <param name="olderThan">早于此时刻的写入才有资格被扫描。</param>
    /// <param name="limit">本轮最多检查的候选数；多轮须公平遍历。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清理任务。</returns>
    Task CollectOrphansAsync(Func<string, CancellationToken, Task<bool>> retireUnreferenced,
        DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);
}
