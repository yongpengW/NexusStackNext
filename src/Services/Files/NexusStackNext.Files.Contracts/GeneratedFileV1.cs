namespace NexusStackNext.Files.Contracts;

/// <summary>私有成果协议 v1 的不可变上传描述。</summary>
/// <param name="OwnerId">精确归属。</param>
/// <param name="SourceExportId">来源导出。</param>
/// <param name="Sha256">完整字节摘要。</param>
/// <param name="Length">完整字节数。</param>
/// <param name="Format">白名单格式。</param>
/// <param name="FormatVersion">格式版本。</param>
/// <param name="ColumnSetVersion">列集版本。</param>
public sealed record GeneratedFileDescriptionV1(string OwnerId, Guid SourceExportId, string Sha256, long Length,
    string Format, int FormatVersion, int ColumnSetVersion);

/// <summary>持久上传裁决；不包含存储句柄或下载许可。</summary>
/// <param name="FileId">稳定文件标识。</param>
/// <param name="UploadId">上传身份。</param>
/// <param name="Stage">Pending 或 Staged。</param>
/// <param name="AcceptedAt">首次接受时间。</param>
/// <param name="StageExpiresAt">首次暂存期限。</param>
/// <param name="SealedAt">首次完整封存时间。</param>
/// <param name="Description">不可变完整描述。</param>
public sealed record GeneratedFileReceiptV1(long FileId, Guid UploadId, string Stage, DateTimeOffset AcceptedAt,
    DateTimeOffset StageExpiresAt, DateTimeOffset? SealedAt, GeneratedFileDescriptionV1 Description)
{
    /// <summary>首次登记持久保存的证书生产者命名空间；调用方必须与预期来源校验。</summary>
    public string Producer { get; init; } = string.Empty;
}

/// <summary>发布请求；不能用另一身份替换成果。</summary>
/// <param name="PublicationId">生产者唯一发布身份。</param>
public sealed record GeneratedFilePublicationV1(Guid PublicationId);

/// <summary>不可变发布历史；当前是否可下载由独立可用性协议决定。</summary>
/// <param name="FileId">原文件。</param>
/// <param name="UploadId">原上传。</param>
/// <param name="PublicationId">首次发布身份。</param>
/// <param name="PublishedAt">首次数据库发布裁决时间。</param>
/// <param name="ExpiresAt">首次下载截止时间。</param>
/// <param name="Description">不可变完整描述。</param>
public sealed record GeneratedFilePublicationReceiptV1(long FileId, Guid UploadId, Guid PublicationId,
    DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt, GeneratedFileDescriptionV1 Description)
{
    /// <summary>原候选持久保存的证书生产者命名空间，不由发布请求指定。</summary>
    public string Producer { get; init; } = string.Empty;
}

/// <summary>当前可用性，不替代历史发布裁决。</summary>
/// <param name="FileId">原文件。</param>
/// <param name="State">Pending/Staged/Available/Expired/Deleted/StorageUnavailable。</param>
/// <param name="CleanupCompleted">是否已持久确认清除终态字节；404或到期本身不证明此项。</param>
public sealed record GeneratedFileAvailabilityV1(long FileId, string State, bool CleanupCompleted = false);
