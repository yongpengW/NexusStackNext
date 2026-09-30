using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Files.Domain.Stored;

/// <summary>文件标识。</summary>
public sealed record StoredFileId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public StoredFileId(long value)
        : base(value)
    {
    }
}

/// <summary>
/// 文件名。
/// <para><b>拒绝路径分隔符与相对路径片段</b>——这是防目录穿越的第一道闸。</para>
/// </summary>
public sealed class FileName : ValueObject
{
    /// <summary>最大长度。</summary>
    public const int MaxLength = 255;

    private FileName(string value) => Value = value;

    /// <summary>规范化后的文件名。</summary>
    public string Value { get; }

    /// <summary>构造文件名。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<FileName> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<FileName>(new Error("files.file_name.empty", "文件名不能为空。"));
        }

        if (trimmed.Length > MaxLength)
        {
            return Result.Failure<FileName>(new Error("files.file_name.too_long", $"文件名不能超过 {MaxLength} 个字符。"));
        }

        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains('\0', StringComparison.Ordinal)
            || trimmed is "." or "..")
        {
            return Result.Failure<FileName>(new Error(
                "files.file_name.unsafe",
                "文件名不能包含路径分隔符，也不能是 . 或 .."));
        }

        return Result.Success(new FileName(trimmed));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// 已存储的文件。
/// <para>
/// 它只持有 <see cref="StorageKey"/>（一个<b>不透明</b>的存储句柄）与元数据，
/// 不持有字节、也不知道自己是存在本地磁盘还是对象存储上。
/// </para>
/// </summary>
public sealed class StoredFile : AggregateRoot<StoredFileId>
{
    private StoredFile(StoredFileId id, FileName name, string contentType, string? ownerId, DateTimeOffset uploadedAt)
        : base(id)
    {
        Name = name;
        ContentType = contentType;
        OwnerId = ownerId;
        UploadedAt = uploadedAt;
    }

    /// <summary>文件名。</summary>
    public FileName Name { get; }

    /// <summary>内容类型。</summary>
    public string ContentType { get; }

    /// <summary>归属者标识；公共文件为 <c>null</c>。</summary>
    public string? OwnerId { get; }

    /// <summary>上传时刻。</summary>
    public DateTimeOffset UploadedAt { get; }

    /// <summary>存储句柄；写入成功前为空。</summary>
    public string? StorageKey { get; private set; }

    /// <summary>字节数。</summary>
    public long Size { get; private set; }

    /// <summary>是否已删除（软删）。</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>是否已有可读取的内容。</summary>
    public bool IsStored => StorageKey is not null;

    /// <summary>登记一个待写入的文件。</summary>
    /// <param name="id">标识。</param>
    /// <param name="name">文件名。</param>
    /// <param name="contentType">内容类型。</param>
    /// <param name="ownerId">归属者标识。</param>
    /// <param name="uploadedAt">上传时刻。</param>
    /// <returns>成功时返回聚合。</returns>
    public static Result<StoredFile> Register(
        StoredFileId id,
        FileName name,
        string? contentType,
        string? ownerId,
        DateTimeOffset uploadedAt)
    {
        ArgumentNullException.ThrowIfNull(name);

        return string.IsNullOrWhiteSpace(contentType)
            ? Result.Failure<StoredFile>(new Error("files.content_type.empty", "内容类型不能为空。"))
            : Result.Success(new StoredFile(id, name, contentType.Trim(), ownerId, uploadedAt));
    }

    /// <summary>标记内容已写入存储。</summary>
    /// <param name="storageKey">存储句柄。</param>
    /// <param name="size">字节数。</param>
    /// <returns>成功，或参数非法。</returns>
    public Result MarkStored(string? storageKey, long size)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return Result.Failure(new Error("files.storage_key.empty", "存储句柄不能为空。"));
        }

        if (size < 0)
        {
            return Result.Failure(new Error("files.size.negative", "文件大小不能为负。"));
        }

        StorageKey = storageKey;
        Size = size;
        return Changed();
    }

    /// <summary>软删除。重复删除不是改变。</summary>
    /// <returns>成功。</returns>
    public Result Delete()
    {
        if (IsDeleted)
        {
            return Result.Success();
        }

        IsDeleted = true;
        return Changed();
    }
}

/// <summary>
/// 字节存储端口。
/// <para>
/// <b>刻意只有三个方法。</b>参照仓库的 <c>IFileStorage</c> 有 14 个成员，
/// 而<b>没有任何一个实现支持全部成员</b>——<c>AliyunFileStorage.GetAbsolutePath</c> 抛
/// <c>NotImplementedException</c>，偏偏视频上传路径会调到它，于是那条路一上传就崩。
/// </para>
/// <para>
/// 一个实现不了 <c>GetAbsolutePath</c>（因为它根本没有本地路径）的存储，
/// 在这里<b>根本不需要实现这个方法</b>——因为它在另一个端口上（见 <see cref="IFileUrlProvider"/>）。
/// 做不到的能力不给接口，就没有"声明了却抛异常"的中间状态。
/// </para>
/// </summary>
public interface IFileStore
{
    /// <summary>写入字节，返回不透明的存储句柄。</summary>
    /// <param name="content">内容流。</param>
    /// <param name="contentType">内容类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存储句柄。</returns>
    Task<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>打开读取流。</summary>
    /// <param name="storageKey">存储句柄。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>内容流。</returns>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>删除内容。</summary>
    /// <param name="storageKey">存储句柄。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 可签发访问链接的存储能力。
/// <para>只有能给出可访问 URL 的存储（对象存储）才实现它；本地磁盘存储不实现，调用方因此不会误用它。</para>
/// </summary>
public interface IFileUrlProvider
{
    /// <summary>签发一个有期限的读取链接。</summary>
    /// <param name="storageKey">存储句柄。</param>
    /// <param name="lifetime">有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可访问的地址。</returns>
    Task<Uri> GetReadUrlAsync(string storageKey, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
