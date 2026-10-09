using System.Security.Cryptography;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Application;

/// <summary>整个宿主共用的成果流预算与首次裁决期限。</summary>
public sealed class GeneratedFileOptions
{
    /// <summary>构造有界资源策略。</summary>
    /// <param name="maxBytes">最多32 MiB。</param>
    /// <param name="maxConcurrentUploads">同时持有字节与元数据保护的请求数。</param>
    /// <param name="uploadTimeoutSeconds">一次上传的流/存储/提交预算。</param>
    /// <param name="stageLifetimeSeconds">首次暂存期限。</param>
    /// <param name="downloadLifetimeSeconds">首次发布后的下载期限。</param>
    public GeneratedFileOptions(long maxBytes = 32 * 1024 * 1024, int maxConcurrentUploads = 1,
        int uploadTimeoutSeconds = 30, int stageLifetimeSeconds = 86400, int downloadLifetimeSeconds = 604800)
    {
        if (maxBytes is < 1 or > 32 * 1024 * 1024 || maxConcurrentUploads is < 1 or > 4
            || uploadTimeoutSeconds is < 1 or > 120 || stageLifetimeSeconds is < 1 or > 172800 || downloadLifetimeSeconds is < 1 or > 2592000)
        { throw new ArgumentOutOfRangeException(nameof(maxBytes), "成果预算或期限超出允许范围。"); }
        Uploads = new(maxBytes, maxConcurrentUploads);
        UploadTimeout = TimeSpan.FromSeconds(uploadTimeoutSeconds);
        StageLifetime = TimeSpan.FromSeconds(stageLifetimeSeconds);
        DownloadLifetime = TimeSpan.FromSeconds(downloadLifetimeSeconds);
    }

    /// <summary>首次暂存期限。</summary>
    public TimeSpan StageLifetime { get; }
    /// <summary>一次上传预算。</summary>
    public TimeSpan UploadTimeout { get; }
    /// <summary>首次发布后的下载期限。</summary>
    public TimeSpan DownloadLifetime { get; }
    /// <summary>本宿主允许的最大实际成果字节数。</summary>
    public long MaxBytes => Uploads.MaxBytes;
    internal FileUploadLimits Uploads { get; }
}

/// <summary>成果协议：只在完整实际内容验证后封存，流写入不占数据库事务。</summary>
/// <param name="files">候选本地裁决。</param>
/// <param name="store">字节存储。</param>
/// <param name="ids">文件标识。</param>
/// <param name="options">宿主共用预算。</param>
public sealed class GeneratedFileService(IGeneratedFileRepository files, IFileStore store, IIdGenerator ids, GeneratedFileOptions options)
{
    /// <summary>幂等登记不可变描述。</summary>
    /// <param name="producer">证书认证得到的生产者。</param>
    /// <param name="uploadId">上传身份。</param>
    /// <param name="description">版本化固定描述。</param>
    /// <param name="token">取消。</param>
    /// <returns>原持久上传回执。</returns>
    public async Task<Result<GeneratedFileReceiptV1>> RegisterAsync(string producer, Guid uploadId, GeneratedFileDescriptionV1 description,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        var candidate = FileCandidate.Create(producer, uploadId, description.SourceExportId, description.OwnerId,
            description.Sha256, description.Length, description.Format, description.FormatVersion, description.ColumnSetVersion, DateTimeOffset.MinValue);
        if (candidate.IsFailure) { return Result.Failure<GeneratedFileReceiptV1>(candidate.Error); }
        if (description.Length > options.Uploads.MaxBytes) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.too_large", "内容超过成果上限。")); }
        try
        {
            var registered = await files.RegisterCandidateAsync(new StoredFileId(ids.NextId()), candidate.Value, options.StageLifetime, token).ConfigureAwait(false);
            return registered.IsSuccess ? Result.Success(Receipt(registered.Value)) : Result.Failure<GeneratedFileReceiptV1>(registered.Error);
        }
        catch (FileAuditCapacityException error) { return Result.Failure<GeneratedFileReceiptV1>(error.Reason); }
    }

    /// <summary>验证完整实际流并原子封存；重放也验证实际内容。</summary>
    /// <param name="producer">证书认证得到的生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="content">实际请求流。</param>
    /// <param name="token">取消。</param>
    /// <returns>原封存回执。</returns>
    public async Task<Result<GeneratedFileReceiptV1>> SealAsync(string producer, Guid uploadId, Stream content, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!options.Uploads.TryEnter()) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.upload_busy", "成果上传名额已用完。")); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(options.UploadTimeout);
        try
        {
            var file = await files.FindCandidateAsync(producer, uploadId, budget.Token).ConfigureAwait(false);
            if (file?.Candidate is not { } candidate) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.not_found", "候选不存在。")); }
            if (candidate.Length > options.MaxBytes) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.too_large", "内容超过当前成果预算。")); }
            using var verified = new VerifiedFileStream(content, candidate.Length, candidate.Sha256);
            if (candidate.SealedAt is not null)
            {
                await verified.CopyToAsync(Stream.Null, budget.Token).ConfigureAwait(false);
                return Result.Success(Receipt(file));
            }
            await using var write = await store.WriteAsync(verified, file.ContentType, budget.Token).ConfigureAwait(false);
            if (!verified.IsVerified) { throw new CandidateContentMismatchException(); }
            var sealedFile = await files.SealCandidateAsync(producer, uploadId, write.StorageKey, verified.BytesRead, candidate.Sha256, budget.Token).ConfigureAwait(false);
            return sealedFile.IsSuccess ? Result.Success(Receipt(sealedFile.Value)) : Result.Failure<GeneratedFileReceiptV1>(sealedFile.Error);
        }
        catch (CandidateContentMismatchException) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.candidate.conflict", "实际长度或摘要与描述不符。")); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.upload_timeout", "成果上传预算已用尽。")); }
        catch (FileAuditCapacityException error) { return Result.Failure<GeneratedFileReceiptV1>(error.Reason); }
        catch (IOException) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.content_missing", "字节存储暂不可用。")); }
        catch (UnauthorizedAccessException) { return Result.Failure<GeneratedFileReceiptV1>(new Error("files.content_missing", "字节存储暂不可用。")); }
        finally { options.Uploads.Exit(); }
    }

    /// <summary>恢复原上传历史。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="token">取消。</param>
    /// <returns>原持久回执。</returns>
    public async Task<GeneratedFileReceiptV1?> ReadAsync(string producer, Guid uploadId, CancellationToken token = default)
    {
        var file = await files.FindCandidateAsync(producer, uploadId, token).ConfigureAwait(false);
        return file is null ? null : Receipt(file);
    }

    /// <summary>幂等发布已封存的候选。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="publicationId">唯一发布身份。</param>
    /// <param name="token">取消。</param>
    /// <returns>原发布历史。</returns>
    public async Task<Result<GeneratedFilePublicationReceiptV1>> PublishAsync(string producer, Guid uploadId, Guid publicationId, CancellationToken token = default)
    {
        try
        {
            var result = await files.PublishCandidateAsync(producer, uploadId, publicationId, options.DownloadLifetime, token).ConfigureAwait(false);
            return result.IsSuccess ? Result.Success(PublicationReceipt(result.Value)) : Result.Failure<GeneratedFilePublicationReceiptV1>(result.Error);
        }
        catch (FileAuditCapacityException error) { return Result.Failure<GeneratedFilePublicationReceiptV1>(error.Reason); }
    }

    /// <summary>恢复原发布历史，不重新判断或延长期限。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="publicationId">原发布身份。</param>
    /// <param name="token">取消。</param>
    /// <returns>原发布历史。</returns>
    public async Task<GeneratedFilePublicationReceiptV1?> ReadPublicationAsync(string producer, Guid publicationId, CancellationToken token = default)
    {
        var file = await files.FindPublicationAsync(producer, publicationId, token).ConfigureAwait(false);
        return file is null ? null : PublicationReceipt(file);
    }

    /// <summary>观察当前可用性；不向生产者返回文件字节。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="token">取消。</param>
    /// <returns>当前可用性。</returns>
    public async Task<GeneratedFileAvailabilityV1?> AvailabilityAsync(string producer, Guid uploadId, CancellationToken token = default)
    {
        var file = await files.FindCandidateAsync(producer, uploadId, token).ConfigureAwait(false);
        return file is null ? null : await ObserveAvailabilityAsync(file, token).ConfigureAwait(false);
    }

    /// <summary>当前有效归属者观察自己的已发布成果。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效会话身份。</param>
    /// <param name="token">取消。</param>
    /// <returns>自己的当前状态，或不存在。</returns>
    public async Task<GeneratedFileAvailabilityV1?> OwnedAvailabilityAsync(StoredFileId id, string ownerId, CancellationToken token = default)
    {
        var file = await files.FindOwnedCandidateAsync(id, ownerId, token).ConfigureAwait(false);
        return file is null ? null : await ObserveAvailabilityAsync(file, token).ConfigureAwait(false);
    }

    private async Task<GeneratedFileAvailabilityV1> ObserveAvailabilityAsync(StoredFile file, CancellationToken token)
    {
        var candidate = file.Candidate!;
        var now = await files.ReadNowAsync(token).ConfigureAwait(false);
        if (candidate.ExpiredAt is not null) { return new(file.Id.Value, "Expired", file.BytesRemovedAt is not null); }
        if (file.IsDeleted) { return new(file.Id.Value, "Deleted", file.BytesRemovedAt is not null); }
        if (now >= (candidate.ExpiresAt ?? candidate.StageExpiresAt)) { return new(file.Id.Value, "Expired"); }
        if (candidate.PublishedAt is null) { return new(file.Id.Value, candidate.SealedAt is null ? "Pending" : "Staged"); }
        try
        {
            await using var content = await store.OpenReadAsync(file.StorageKey!, token).ConfigureAwait(false);
            return new(file.Id.Value, "Available");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(file.Id.Value, "StorageUnavailable"); }
    }

    internal static GeneratedFileReceiptV1 Receipt(StoredFile file) => new(file.Id.Value, file.Candidate!.UploadId,
        file.Candidate.SealedAt is null ? "Pending" : "Staged", file.UploadedAt, file.Candidate.StageExpiresAt, file.Candidate.SealedAt, Description(file.Candidate));

    private static GeneratedFilePublicationReceiptV1 PublicationReceipt(StoredFile file) => new(file.Id.Value, file.Candidate!.UploadId,
        file.Candidate.PublicationId!.Value, file.Candidate.PublishedAt!.Value, file.Candidate.ExpiresAt!.Value, Description(file.Candidate));

    private static GeneratedFileDescriptionV1 Description(FileCandidate candidate) => new(candidate.OwnerId, candidate.SourceExportId,
        candidate.Sha256, candidate.Length, candidate.Format, candidate.FormatVersion, candidate.ColumnSetVersion);
}

internal sealed class CandidateContentMismatchException() : IOException("实际内容不符。");

internal sealed class VerifiedFileStream(Stream source, long expectedLength, string expectedHash) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long BytesRead { get; private set; }
    public bool IsVerified { get; private set; }
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) { return 0; }
        var count = source.Read(buffer[..ReadSize(buffer.Length)]);
        Record(buffer[..count]);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) { return 0; }
        var count = await source.ReadAsync(buffer[..ReadSize(buffer.Length)], cancellationToken).ConfigureAwait(false);
        Record(buffer.Span[..count]);
        return count;
    }
    private int ReadSize(int requested) => (int)Math.Min(requested, expectedLength - BytesRead + 1);
    private void Record(ReadOnlySpan<byte> bytes)
    {
        if (IsVerified) { if (bytes.Length != 0) { throw new CandidateContentMismatchException(); } return; }
        if (bytes.Length > expectedLength - BytesRead) { throw new CandidateContentMismatchException(); }
        if (bytes.Length != 0) { _hash.AppendData(bytes); BytesRead += bytes.Length; return; }
        if (BytesRead != expectedLength || !string.Equals(Convert.ToHexString(_hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
        { throw new CandidateContentMismatchException(); }
        IsVerified = true;
    }
    protected override void Dispose(bool disposing) { if (disposing) { _hash.Dispose(); } base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
