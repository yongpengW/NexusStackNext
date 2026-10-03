using System.Globalization;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Application;

/// <summary>文件恢复循环的有界配置。</summary>
public sealed class FileRecoveryOptions
{
    /// <summary>构造恢复配置。</summary>
    /// <param name="intervalSeconds">循环间隔，1 至 3600 秒。</param>
    /// <param name="batchSize">每轮最多清理数，1 至 1000。</param>
    /// <param name="retryDelaySeconds">存储失败后的重试间隔，1 至 3600 秒。</param>
    /// <param name="orphanAgeSeconds">孤儿候选的最小年龄，至少一秒；年龄不替代写入保护。</param>
    public FileRecoveryOptions(int intervalSeconds = 30, int batchSize = 64, int retryDelaySeconds = 30, int orphanAgeSeconds = 3600)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intervalSeconds, 3600);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelaySeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retryDelaySeconds, 3600);
        ArgumentOutOfRangeException.ThrowIfLessThan(orphanAgeSeconds, 1);
        Interval = TimeSpan.FromSeconds(intervalSeconds);
        RetryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        BatchSize = batchSize;
        OrphanAge = TimeSpan.FromSeconds(orphanAgeSeconds);
    }

    /// <summary>轮次间隔。</summary>
    public TimeSpan Interval { get; }
    /// <summary>存储失败后的延迟。</summary>
    public TimeSpan RetryDelay { get; }
    /// <summary>每轮最多文件数。</summary>
    public int BatchSize { get; }
    /// <summary>开始扫描孤儿的最小年龄。</summary>
    public TimeSpan OrphanAge { get; }
}

/// <summary>由文件聚合上的持久状态恢复删除；每次只提交一个聚合。</summary>
/// <param name="store">字节存储。</param>
/// <param name="files">文件元数据。</param>
/// <param name="clock">时钟。</param>
/// <param name="options">轮次与重试上限。</param>
/// <param name="orphans">持有写入保护的孤儿回收适配器。</param>
/// <param name="observations">逐文件后台执行观察。</param>
public sealed class FileRecovery(IFileStore store, IStoredFileRepository files, IClock clock, FileRecoveryOptions options, IOrphanFileStore orphans,
    IBackgroundExecutionObservation? observations = null)
{
    /// <summary>尝试清理一次；存储失败留下下一次重试时间。</summary>
    /// <param name="file">已经软删除的文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已清除字节并保存完成状态时为真。</returns>
    public async Task<bool> CompleteDeletionAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.IsDeleted) { throw new InvalidOperationException("未请求删除的文件不能清理。"); }
        if (file.BytesRemovedAt is not null) { return true; }
        // 保存被拒绝时，调用方持有的快照仍必须表达已提交状态，允许复用它重试。
        file = file.Snapshot();
        var originalVersion = file.Version;
        try
        {
            try
            {
                if (file.StorageKey is not null) { await store.DeleteAsync(file.StorageKey, cancellationToken).ConfigureAwait(false); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                file.PostponeCleanup(clock.UtcNow + options.RetryDelay);
                await files.SaveAsync(file, originalVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }
            file.ConfirmBytesRemoved(clock.UtcNow);
            await files.SaveAsync(file, originalVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FileAuditCapacityException)
        {
            // 删除已受理；字节可能已清除，完成事实未提交时仍保留持久待办。
            return false;
        }
        catch (FileMetadataConflictException)
        {
            var current = await files.FindDeletedAsync(file.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new FileMetadataConflictException();
            return current.BytesRemovedAt is not null;
        }
    }

    /// <summary>恢复到期的有界删除批次。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恢复任务。</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var pending = await files.PendingDeletionsAsync(clock.UtcNow, options.BatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var file in pending)
        {
            if (observations is null) { await CompleteDeletionAsync(file, cancellationToken).ConfigureAwait(false); }
            else
            {
                await observations.ObserveAsync(new RecoveryExecutionDescriptor("files.deletion.recover", "stored-file", "int64", file.Id.Value.ToString(CultureInfo.InvariantCulture)),
                    async () => new BackgroundExecutionInput<StoredFile>(file, await files.ReadDeletionOriginAsync(file.Id, cancellationToken).ConfigureAwait(false)),
                    current => CompleteDeletionAsync(current, cancellationToken),
                    completed => completed ? BackgroundExecutionOutcome.Completed : BackgroundExecutionOutcome.Deferred,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        await orphans.CollectOrphansAsync(files.RetireUnreferencedStorageAsync, clock.UtcNow - options.OrphanAge,
            options.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
