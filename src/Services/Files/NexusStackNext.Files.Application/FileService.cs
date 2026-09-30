using NexusStackNext.BuildingBlocks.Application.Ids;
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

    /// <summary>保存（新增或更新）。</summary>
    /// <param name="file">文件聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default);
}

/// <summary>
/// 上传、下载、删除。
///
/// <para><b>写入顺序是有讲究的：先写字节，再标记已存储。</b>
/// 反过来会留下一种很难查的状态——元数据说"已存储"，而字节根本不在。
/// 先写字节的最坏情况只是留下一份没人引用的字节（可以被清理任务回收）。</para>
///
/// <para><b>接口上的一个真实约束</b>：<see cref="UploadAsync"/> 要求内容流**可定位**，
/// 因为要在写入前就知道字节数。这不是实现细节，是调用方必须知道的事实，
/// 所以它写在接口文档里，而不是等到运行时抛异常。</para>
/// </summary>
/// <param name="store">字节存储端口。</param>
/// <param name="files">元数据仓储。</param>
/// <param name="ids">标识生成器。</param>
/// <param name="clock">时钟。</param>
public sealed class FileService(
    IFileStore store,
    IStoredFileRepository files,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>上传一个文件。</summary>
    /// <param name="name">文件名（已由领域校验，拒绝路径分隔符）。</param>
    /// <param name="contentType">内容类型。</param>
    /// <param name="content"><b>可定位的</b>内容流。</param>
    /// <param name="ownerId">归属者；公共文件为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回登记后的聚合。</returns>
    /// <exception cref="ArgumentNullException">参数为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">内容流不可定位。</exception>
    public async Task<Result<StoredFile>> UploadAsync(
        FileName name,
        string contentType,
        Stream content,
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanSeek)
        {
            throw new ArgumentException("上传要求可定位的内容流，以便在写入前确定字节数。", nameof(content));
        }

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

        var file = registered.Value;
        var size = content.Length;

        // 先写字节。这一步失败就不会留下任何元数据。
        var storageKey = await store.WriteAsync(content, file.ContentType, cancellationToken).ConfigureAwait(false);

        var marked = file.MarkStored(storageKey, size);
        if (marked.IsFailure)
        {
            return Result.Failure<StoredFile>(marked.Error);
        }

        await files.SaveAsync(file, cancellationToken).ConfigureAwait(false);
        return Result.Success(file);
    }

    /// <summary>打开一个文件的内容流。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回聚合与内容流；文件不存在或没有内容时失败。</returns>
    public async Task<Result<(StoredFile File, Stream Content)>> OpenAsync(
        StoredFileId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var file = await files.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null)
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
        catch (IOException exception)
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.content_missing",
                $"文件没有内容：{id.Value}。（{exception.Message}）"));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<(StoredFile, Stream)>(new Error(
                "files.content_missing",
                $"文件没有内容：{id.Value}。（{exception.Message}）"));
        }

        return Result.Success((file, content));
    }

    /// <summary>读取一个文件的元数据（不碰字节）。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    public Task<StoredFile?> DescribeAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return files.FindAsync(id, cancellationToken);
    }

    /// <summary>
    /// 删除一个文件：<b>先软删元数据，再删字节</b>。
    /// <para>顺序与上传相反，理由也一样——先软删之后，最坏情况留下一份无人引用的字节；
    /// 反过来则会出现"元数据还在、字节没了"的 500。</para>
    /// </summary>
    /// <param name="id">文件标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或文件不存在。</returns>
    public async Task<Result> DeleteAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var file = await files.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return Result.Failure(new Error("files.not_found", $"文件不存在：{id.Value}。"));
        }

        var deleted = file.Delete();
        if (deleted.IsFailure)
        {
            return deleted;
        }

        await files.SaveAsync(file, cancellationToken).ConfigureAwait(false);

        if (file.StorageKey is not null)
        {
            try
            {
                await store.DeleteAsync(file.StorageKey, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // 元数据已软删，字节删不掉——文件对用户已经不可见，但字节还在。
                // **返回成功会让这件事永远没人知道**，所以给一个可区分的失败码，
                // 让调用方能告警或重试，而不是拿到 204 以为干净了。
                return Result.Failure(new Error(
                    "files.bytes_not_removed",
                    $"元数据已软删，但字节没能删除：{file.StorageKey}。（{exception.Message}）"));
            }
            catch (UnauthorizedAccessException exception)
            {
                return Result.Failure(new Error(
                    "files.bytes_not_removed",
                    $"元数据已软删，但字节没能删除：{file.StorageKey}。（{exception.Message}）"));
            }
        }

        return Result.Success();
    }
}
