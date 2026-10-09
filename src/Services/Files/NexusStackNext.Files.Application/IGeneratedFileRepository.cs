using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Application;

/// <summary>候选身份的本地原子裁决；PostgreSQL 与显式开发 Memory 各自实现。</summary>
public interface IGeneratedFileRepository
{
    /// <summary>精确归属者读取已发布成果的当前状态；未发布候选不可见。</summary>
    /// <param name="id">文件标识。</param>
    /// <param name="ownerId">当前有效会话身份。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>自己的已发布成果，包括终态。</returns>
    Task<StoredFile?> FindOwnedCandidateAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default);

    /// <summary>有界发现到期候选；最终裁决必须重新读取当前状态与时间。</summary>
    /// <param name="limit">本轮最多数。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>到期待办。</returns>
    Task<IReadOnlyList<StoredFile>> ReadExpiredCandidatesAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>对一个候选重新判定到期并持久请求清理。</summary>
    /// <param name="producer">原生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>当前持久状态。</returns>
    Task<Result<StoredFile>> ExpireCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default);

    /// <summary>取所属存储当前裁决时钟；生产实现来自数据库。</summary>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>所属时钟。</returns>
    Task<DateTimeOffset> ReadNowAsync(CancellationToken cancellationToken = default);

    /// <summary>恢复指定生产者的原发布裁决，包含终态。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="publicationId">原发布身份。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>原候选。</returns>
    Task<StoredFile?> FindPublicationAsync(string producer, Guid publicationId, CancellationToken cancellationToken = default);

    /// <summary>幂等发布完整候选；同发布身份不能分配给两个文件。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">原上传身份。</param>
    /// <param name="publicationId">首次发布身份。</param>
    /// <param name="downloadLifetime">首次发布后的期限。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>原持久成果，或冲突。</returns>
    Task<Result<StoredFile>> PublishCandidateAsync(string producer, Guid uploadId, Guid publicationId, TimeSpan downloadLifetime,
        CancellationToken cancellationToken = default);

    /// <summary>按生产者内身份读取，包括终态墓碑。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">上传身份。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>原持久候选。</returns>
    Task<StoredFile?> FindCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default);

    /// <summary>在所属短事务内取得时间并幂等接受描述。</summary>
    /// <param name="id">新候选标识；重放不使用它。</param>
    /// <param name="description">已验证描述；截止时间由裁决重新确定。</param>
    /// <param name="stageLifetime">暂存期限。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>同一候选或描述冲突。</returns>
    Task<Result<StoredFile>> RegisterCandidateAsync(StoredFileId id, FileCandidate description, TimeSpan stageLifetime,
        CancellationToken cancellationToken = default);

    /// <summary>在所属短事务内对实际已验证内容作一次封存裁决。</summary>
    /// <param name="producer">认证生产者。</param>
    /// <param name="uploadId">上传身份。</param>
    /// <param name="storageKey">仍受保护的存储句柄。</param>
    /// <param name="length">实际完整长度。</param>
    /// <param name="sha256">实际完整摘要。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>持久候选，或冲突。</returns>
    Task<Result<StoredFile>> SealCandidateAsync(string producer, Guid uploadId, string storageKey, long length, string sha256,
        CancellationToken cancellationToken = default);
}
