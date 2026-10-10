using NexusStackNext.BuildingBlocks.Domain;


namespace NexusStackNext.Files.Contracts;

/// <summary>跨宿主私有成果协议；只接受冻结描述和固定身份，不携带用户令牌。</summary>
public interface IExportFiles
{
    /// <summary>在渲染前查询原候选，恢复已经封存的实际字节身份。</summary>
    /// <param name="lookup">原上传与格式身份；尚未知晓 ZIP 摘要。</param>
    /// <param name="cancellationToken">独立网络预算。</param>
    /// <returns>经过完整身份校验的原候选；未登记时为空。</returns>
    Task<Result<GeneratedFileReceiptV1?>> FindAsync(ExportFileLookup lookup, CancellationToken cancellationToken = default);

    /// <summary>按原候选身份查询或封存精确字节。</summary>
    /// <param name="upload">固定候选与冻结描述。</param>
    /// <param name="content">可重新定位的完整字节流；调用方拥有其生命周期。</param>
    /// <param name="cancellationToken">执行预算。</param>
    /// <returns>完整验证后的封存回执或稳定错误。</returns>
    Task<Result<GeneratedFileReceiptV1>> StageAsync(ExportFileUpload upload, Stream content, CancellationToken cancellationToken = default);

    /// <summary>恢复或提交同一发布意图；历史发布结果不依赖当前下载可用性。</summary>
    /// <param name="publication">已提交的原意图。</param>
    /// <param name="cancellationToken">交付预算。</param>
    /// <returns>完整验证后的原发布裁决。</returns>
    Task<Result<GeneratedFilePublicationReceiptV1>> PublishAsync(ExportFilePublication publication, CancellationToken cancellationToken = default);

    /// <summary>读取独立当前可用性，不改变导出成功历史。</summary>
    /// <param name="uploadId">原候选身份。</param>
    /// <param name="fileId">原文件标识。</param>
    /// <param name="cancellationToken">读取预算。</param>
    /// <returns>当前可用性。</returns>
    Task<Result<GeneratedFileAvailabilityV1>> AvailabilityAsync(Guid uploadId, long fileId, CancellationToken cancellationToken = default);
}

/// <summary>渲染前已经持久确定的原候选身份。</summary>
/// <param name="UploadId">原上传。</param>
/// <param name="OwnerId">原委托归属。</param>
/// <param name="SourceExportId">原委托。</param>
/// <param name="Format">原格式。</param>
public sealed record ExportFileLookup(Guid UploadId, string OwnerId, Guid SourceExportId, string Format);

/// <summary>固定候选描述。</summary>
/// <param name="UploadId">原候选身份。</param>
/// <param name="Description">原冻结描述。</param>
public sealed record ExportFileUpload(Guid UploadId, GeneratedFileDescriptionV1 Description);

/// <summary>一次持久选定后的完整发布引用。</summary>
/// <param name="UploadId">原候选身份。</param>
/// <param name="FileId">原文件。</param>
/// <param name="PublicationId">原发布意图。</param>
/// <param name="Description">原冻结描述。</param>
public sealed record ExportFilePublication(Guid UploadId, long FileId, Guid PublicationId, GeneratedFileDescriptionV1 Description);
