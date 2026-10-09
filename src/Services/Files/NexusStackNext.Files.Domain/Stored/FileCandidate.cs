using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Files.Domain.Stored;

/// <summary>文件聚合所属的不可变候选身份及封存裁决；不单独构成聚合。</summary>
public sealed class FileCandidate
{
    private FileCandidate() { }

    /// <summary>验证候选身份、归属、版本与实际内容的预期值。</summary>
    /// <param name="producer">证书认证得到的生产者。</param>
    /// <param name="uploadId">生产者内唯一上传标识。</param>
    /// <param name="sourceExportId">来源导出标识。</param>
    /// <param name="ownerId">不可变成果归属。</param>
    /// <param name="sha256">完整内容摘要。</param>
    /// <param name="length">完整内容字节数。</param>
    /// <param name="format">固定格式。</param>
    /// <param name="formatVersion">格式版本。</param>
    /// <param name="columnSetVersion">列集版本。</param>
    /// <param name="expiresAt">首次接受时裁定的暂存期限。</param>
    /// <returns>有效候选，或无效描述。</returns>
    public static Result<FileCandidate> Create(string producer, Guid uploadId, Guid sourceExportId, string ownerId,
        string sha256, long length, string format, int formatVersion, int columnSetVersion, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(producer) || producer.Length > 32 || uploadId == Guid.Empty || sourceExportId == Guid.Empty
            || string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128 || ownerId.Any(char.IsControl)
            || sha256 is null || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character))
            || length is < 1 or > 32 * 1024 * 1024 || format != "csv" || formatVersion != 1 || columnSetVersion != 1)
        { return Result.Failure<FileCandidate>(new Error("files.candidate.invalid", "候选描述无效。")); }
        return Result.Success(new FileCandidate
        {
            Producer = producer,
            UploadId = uploadId,
            SourceExportId = sourceExportId,
            OwnerId = ownerId,
            Sha256 = sha256.ToLowerInvariant(),
            Length = length,
            Format = format,
            FormatVersion = formatVersion,
            ColumnSetVersion = columnSetVersion,
            StageExpiresAt = expiresAt,
        });
    }

    /// <summary>可信生产者。</summary>
    public string Producer { get; private set; } = null!;
    /// <summary>生产者内上传身份。</summary>
    public Guid UploadId { get; private set; }
    /// <summary>来源导出身份。</summary>
    public Guid SourceExportId { get; private set; }
    /// <summary>不可变归属。</summary>
    public string OwnerId { get; private set; } = null!;
    /// <summary>规范化完整摘要。</summary>
    public string Sha256 { get; private set; } = null!;
    /// <summary>完整字节数。</summary>
    public long Length { get; private set; }
    /// <summary>固定格式。</summary>
    public string Format { get; private set; } = null!;
    /// <summary>格式版本。</summary>
    public int FormatVersion { get; private set; }
    /// <summary>列集版本。</summary>
    public int ColumnSetVersion { get; private set; }
    /// <summary>首次暂存截止时间。</summary>
    public DateTimeOffset StageExpiresAt { get; private set; }
    /// <summary>完整内容已验证并封存的时间。</summary>
    public DateTimeOffset? SealedAt { get; private set; }
    /// <summary>唯一且不可替换的首次发布身份。</summary>
    public Guid? PublicationId { get; private set; }
    /// <summary>首次持久发布时刻。</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>首次发布裁定的下载截止时间。</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }
    /// <summary>到期清理裁决；区别于归属者主动删除。</summary>
    public DateTimeOffset? ExpiredAt { get; private set; }

    /// <summary>比较不可变描述，不比较裁决时间。</summary>
    /// <param name="other">另一个经过验证的描述。</param>
    /// <returns>是否是同一次上传。</returns>
    public bool HasSameDescription(FileCandidate other) => other is not null && Producer == other.Producer && UploadId == other.UploadId
        && SourceExportId == other.SourceExportId && OwnerId == other.OwnerId && Sha256 == other.Sha256 && Length == other.Length
        && Format == other.Format && FormatVersion == other.FormatVersion && ColumnSetVersion == other.ColumnSetVersion;

    internal FileCandidate Snapshot() => (FileCandidate)MemberwiseClone();
    internal void Seal(DateTimeOffset at) => SealedAt = at;
    internal void Publish(Guid publicationId, DateTimeOffset at, DateTimeOffset expiresAt)
    { PublicationId = publicationId; PublishedAt = at; ExpiresAt = expiresAt; }
    internal void Expire(DateTimeOffset at) => ExpiredAt = at;

    /// <summary>首次接受时以所属裁决的时间重建期限，不修改描述。</summary>
    /// <param name="expiresAt">首次裁定期限。</param>
    /// <returns>独立候选描述。</returns>
    public FileCandidate WithDeadline(DateTimeOffset expiresAt)
    {
        var copy = Snapshot();
        copy.StageExpiresAt = expiresAt;
        return copy;
    }
}
