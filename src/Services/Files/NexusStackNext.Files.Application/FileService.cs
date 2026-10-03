using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Application;

/// <summary>文件元数据仓储端口。</summary>
public interface IStoredFileRepository
{
    /// <summary>按标识查找。<b>已软删的不返回</b>——调用方不该需要自己记得过滤。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default);

    /// <summary>按标识查找已请求删除的文件，供归属检查与状态查询。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已软删除的文件，否则为空。</returns>
    Task<StoredFile?> FindDeletedAsync(StoredFileId id, CancellationToken cancellationToken = default);

    /// <summary>读取首次成功提交删除请求时保存的来源；未知来源保持为空。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>只供执行关联使用的来源，不是授权依据。</returns>
    Task<ExecutionOrigin?> ReadDeletionOriginAsync(StoredFileId id, CancellationToken cancellationToken = default);

    /// <summary>按下一次尝试时间获取有界的待清理文件。</summary>
    /// <param name="now">当前时刻。</param>
    /// <param name="limit">最多处理数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>到期且尚未确认字节清除的文件。</returns>
    Task<IReadOnlyList<StoredFile>> PendingDeletionsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>在与文件保存相同的事务锁下退役无引用句柄；退役后任何迟到的保存都必须被拒绝。</summary>
    /// <param name="storageKey">存储句柄。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已持久退役时为真；仍有文件引用时为假。</returns>
    Task<bool> RetireUnreferencedStorageAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>保存；新记录不传原版本，更新必须提供读取时的版本。</summary>
    /// <param name="file">文件聚合。</param>
    /// <param name="originalVersion">更新前版本；不允许覆盖其他请求已提交的状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="deletionOrigin">首次删除的来源，与删除状态同提交；后续保存不覆盖它。</param>
    /// <returns>任务。</returns>
    Task SaveAsync(StoredFile file, long? originalVersion = null, ExecutionOrigin? deletionOrigin = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 上传、下载、删除。
///
/// <para><b>写入顺序是有讲究的：先写字节，再标记已存储。</b>
/// 反过来会留下一种很难查的状态——元数据说"已存储"，而字节根本不在。
/// 先写字节的最坏情况只是留下一份没人引用的字节（可以被清理任务回收）。</para>
///
/// <para>上传按实际读取的字节计数，不要求流可定位，也不把整份内容放入内存。</para>
/// </summary>
/// <param name="store">字节存储端口。</param>
/// <param name="files">元数据仓储。</param>
/// <param name="ids">标识生成器。</param>
/// <param name="clock">时钟。</param>
/// <param name="limits">宿主共享的上传限制。</param>
/// <param name="recovery">持久删除与恢复。</param>
/// <param name="execution">当前执行关联。</param>
public sealed class FileService(
    IFileStore store,
    IStoredFileRepository files,
    IIdGenerator ids,
    IClock clock,
    FileUploadLimits limits,
    FileRecovery recovery,
    IExecutionContext? execution = null)
{
    /// <summary>上传一个文件。</summary>
    /// <param name="name">文件名（已由领域校验，拒绝路径分隔符）。</param>
    /// <param name="contentType">内容类型。</param>
    /// <param name="content">可读取的内容流；支持没有长度的请求体。</param>
    /// <param name="ownerId">由可信调用方提供的归属者；HTTP 取当前有效会话身份。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回登记后的聚合。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    public async Task<Result<StoredFile>> UploadAsync(
        FileName name,
        string contentType,
        Stream content,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var registered = StoredFile.Register(
            new StoredFileId(ids.NextId()),
            name,
            contentType,
            ownerId,
            clock.UtcNow);

        if (registered.IsFailure)
        {
            return registered;
        }

        if (!limits.TryEnter())
        {
            return Result.Failure<StoredFile>(new Error("files.upload_busy", "当前上传名额已用完，请稍后重试。"));
        }

        try
        {
            var file = registered.Value;
            using var bounded = new UploadReadStream(content, limits.MaxBytes);
            FileWrite write;
            try
            {
                write = await store.WriteAsync(bounded, file.ContentType, cancellationToken).ConfigureAwait(false);
            }
            catch (FileUploadLimitException)
            {
                return Result.Failure<StoredFile>(new Error("files.too_large", "文件超过上传大小限制。"));
            }

            await using (write.ConfigureAwait(false))
            {
                var marked = file.MarkStored(write.StorageKey, bounded.BytesRead);
                if (marked.IsFailure) { return Result.Failure<StoredFile>(marked.Error); }
                await files.SaveAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Result.Success(file);
            }
        }
        finally
        {
            limits.Exit();
        }
    }

    /// <summary>打开一个文件的内容流。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效身份的归属者标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回聚合与内容流；文件不存在或没有内容时失败。</returns>
    public async Task<Result<(StoredFile File, Stream Content)>> OpenAsync(
        StoredFileId id,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var file = await files.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null || !string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.not_found",
                $"文件不存在：{id.Value}。"));
        }

        // 元数据在但字节不在——这是"先标记后写字节"才会产生的状态，这里显式报出来而不是 NRE。
        if (!file.IsStored || file.StorageKey is null)
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.content_missing",
                $"文件没有内容：{id.Value}。"));
        }

        // 元数据在但字节不在——这是数据损坏或迁移出错后的状态。
        // 它必须以**明确的失败**返回，而不是把存储的异常直接抛给调用方：
        // 调用方拿到 Result 才能决定是 404、是 500、还是告警。
        Stream content;
        try
        {
            content = await store.OpenReadAsync(file.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.content_missing",
                "文件内容暂不可用。"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.content_missing",
                "文件内容暂不可用。"));
        }

        return Result.Success((file, content));
    }

    /// <summary>读取一个文件的元数据（不碰字节）。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效身份的归属者标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    public async Task<StoredFile?> DescribeAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var file = await files.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return file is not null && string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal) ? file : null;
    }

    /// <summary>
    /// 删除一个文件：<b>先软删元数据，再删字节</b>。
    /// <para>顺序与上传相反，理由也一样——先软删之后，最坏情况留下一份无人引用的字节；
    /// 反过来则会出现"元数据还在、字节没了"的 500。</para>
    /// </summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效身份的归属者标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功值表示字节是否已清除；未完成的删除由恢复任务继续处理。</returns>
    public async Task<Result<bool>> DeleteAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var file = await files.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? await files.FindDeletedAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null || !string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return Result.Failure<bool>(new Error("files.not_found", $"文件不存在：{id.Value}。"));
        }
        if (!file.IsDeleted)
        {
            var originalVersion = file.Version;
            file.Delete();
            try { await files.SaveAsync(file, originalVersion, execution?.Capture(), cancellationToken).ConfigureAwait(false); }
            catch (FileMetadataConflictException)
            {
                // 归属不可变，删除也不能撤销；另一个请求已推进状态时直接观察其持久结果。
                file = await files.FindDeletedAsync(id, cancellationToken).ConfigureAwait(false)
                    ?? throw new FileMetadataConflictException();
            }
        }
        return Result.Success(await recovery.CompleteDeletionAsync(file, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>查询归属者自己的删除状态。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效会话身份。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否已清除字节；未知、未请求删除或不属于当前身份时为空。</returns>
    public async Task<bool?> DeletionCompletedAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var file = await files.FindDeletedAsync(id, cancellationToken).ConfigureAwait(false);
        return file is not null && string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal) ? file.BytesRemovedAt is not null : null;
    }
}
