using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Auditing.Application;

/// <summary>按当前调查条件接受一份私有 XLSX；只能选择一种证据类别。</summary>
/// <param name="RequestId">本人重试间保留的身份。</param>
/// <param name="Facts">已提交事实筛选；分页不参与导出。</param>
/// <param name="Operations">操作观察筛选；分页不参与导出。</param>
[BackgroundWorkAcceptance]
public sealed record AcceptAuditExport(Guid RequestId, AuditQuery? Facts = null, OperationQuery? Operations = null) : ICommand<AuditExportStatus>;

/// <summary>读取本人原导出的安全元数据。</summary>
/// <param name="ExportId">原导出身份。</param>
public sealed record GetAuditExport(Guid ExportId) : IQuery<AuditExportStatus>;

/// <summary>分页读取本人导出中心。</summary>
/// <param name="Page">页码，1 至 1000。</param>
/// <param name="Limit">页大小，1 至 200。</param>
public sealed record ListAuditExports(int Page = 1, int Limit = 50) : IQuery<IReadOnlyList<AuditExportStatus>>;

/// <summary>取消尚未提交发布意图的本人导出。</summary>
/// <param name="ExportId">原导出身份。</param>
/// <param name="ExpectedVersion">观察到的版本。</param>
public sealed record CancelAuditExport(Guid ExportId, long ExpectedVersion) : ICommand<AuditExportStatus>;

/// <summary>本人恢复原停机委托。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="ExpectedVersion">观察版本。</param>
[BackgroundWorkAcceptance]
public sealed record RetryAuditExport(Guid ExportId, long ExpectedVersion) : ICommand<AuditExportStatus>;

/// <summary>后台领取原持久委托；不映射 HTTP。</summary>
[CommandObservationSuppression("轮询不制造独立业务操作；实际处理有任务观察。")]
public sealed record ClaimAuditExport : ICommand<AuditExportLease?>;

/// <summary>原委托的有限执行权。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="Epoch">当前代次。</param>
/// <param name="LeaseUntil">固定期限。</param>
public sealed record AuditExportLease(Guid ExportId, long Epoch, DateTimeOffset LeaseUntil);

/// <summary>按当前执行权生成或恢复原成果发布。</summary>
/// <param name="Lease">本次有限执行权。</param>
[CommandObservationSuppression("实际生成和恢复由后台任务观察。")]
public sealed record ProcessAuditExport(AuditExportLease Lease) : ICommand<AuditExportStatus>;

/// <summary>本人查看原成果的当前可用性。</summary>
/// <param name="ExportId">原导出。</param>
public sealed record GetAuditExportArtifact(Guid ExportId) : IQuery<AuditExportArtifact>;

/// <summary>Files 的当前裁决，不覆盖导出成功历史。</summary>
/// <param name="FileId">原文件。</param>
/// <param name="State">当前可用性。</param>
/// <param name="DownloadPath">可用时的私有相对路径。</param>
public sealed record AuditExportArtifact(long FileId, string State, string? DownloadPath);

/// <summary>本人原调查导出的元数据，不含冻结行。</summary>
/// <param name="ExportId">导出身份。</param>
/// <param name="RequestId">原请求身份。</param>
/// <param name="Version">聚合版本。</param>
/// <param name="Kind">facts 或 operations。</param>
/// <param name="State">后台阶段。</param>
/// <param name="AcceptedAt">首次接受时刻。</param>
/// <param name="FrozenAt">一致观察开始时刻。</param>
/// <param name="From">固定来源时间下界。</param>
/// <param name="To">固定来源时间上界。</param>
/// <param name="RowCount">固定快照行数。</param>
/// <param name="Audit">行审计元数据。</param>
public sealed record AuditExportStatus(Guid ExportId, Guid RequestId, long Version, string Kind, string State,
    DateTimeOffset AcceptedAt, DateTimeOffset FrozenAt, DateTimeOffset From, DateTimeOffset To, int RowCount, EntityAuditMetadata? Audit)
{
    /// <summary>当前执行代次。</summary>
    public long Epoch { get; init; }
    /// <summary>有限自动尝试次数。</summary>
    public int Attempts { get; init; }
    /// <summary>有限执行期限。</summary>
    public DateTimeOffset? LeaseUntil { get; init; }
    /// <summary>已选定的原文件。</summary>
    public long? FileId { get; init; }
    /// <summary>原文件摘要。</summary>
    public string? ArtifactDigest { get; init; }
    /// <summary>原字节长度。</summary>
    public long? ArtifactLength { get; init; }
    /// <summary>原发布时刻。</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>原截止时间。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>最近稳定错误码。</summary>
    public string? ErrorCode { get; init; }
}
