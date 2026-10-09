using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Pricing.Application;

/// <summary>持久接受固定列报价导出；归属只能来自当前身份。</summary>
/// <param name="RequestId">同一用户在重试间保留的非空标识。</param>
/// <param name="ItemIds">白名单对象筛选；空集合表示全部报价。</param>
/// <param name="CalculationState">Any、Pending、Stale 或 Current。</param>
/// <param name="FormatVersion">固定 CSV 格式版本 1。</param>
/// <param name="ColumnSetVersion">固定列集版本 1。</param>
[BackgroundWorkAcceptance]
public sealed record AcceptPricingExport(Guid RequestId, IReadOnlyList<Guid> ItemIds, string CalculationState = "Any",
    int FormatVersion = 1, int ColumnSetVersion = 1) : ICommand<PricingExportStatus>;

/// <summary>读取当前用户自己的一次导出，不返回冻结报价载荷。</summary>
/// <param name="ExportId">导出标识。</param>
public sealed record GetPricingExport(Guid ExportId) : IQuery<PricingExportStatus>;

/// <summary>本人下载中心的固定筛选与不可变接受时间游标。</summary>
/// <param name="Limit">默认五十，最多两百。</param>
/// <param name="State">固定阶段筛选。</param>
/// <param name="AcceptedFrom">接受时间下界。</param>
/// <param name="AcceptedThrough">接受时间上界。</param>
/// <param name="Cursor">上一页返回的原游标；状态、时间和归属筛选不能改变。</param>
public sealed record ListPricingExports(int Limit = 50, string? State = null, DateTimeOffset? AcceptedFrom = null,
    DateTimeOffset? AcceptedThrough = null, string? Cursor = null) : IQuery<PricingExportPage>;

/// <summary>有界本人元数据页，不含冻结载荷。</summary>
/// <param name="Items">本页状态。</param>
/// <param name="NextCursor">下一页游标。</param>
public sealed record PricingExportPage(IReadOnlyList<PricingExportStatus> Items, string? NextCursor);

/// <summary>本人观察原成果的独立当前可用性。</summary>
/// <param name="ExportId">原导出。</param>
public sealed record GetPricingExportArtifact(Guid ExportId) : IQuery<PricingExportArtifact>;

/// <summary>安全文件引用；下载仍由 Files 验证当前会话与精确归属。</summary>
/// <param name="FileId">原文件。</param>
/// <param name="PublicationId">原发布意图。</param>
/// <param name="State">当前可用性，不覆盖成功历史。</param>
/// <param name="DownloadPath">仅当前 Available 时给出相对下载路径。</param>
/// <param name="PublishedAt">原发布时间。</param>
/// <param name="ExpiresAt">原截止时间。</param>
public sealed record PricingExportArtifact(long FileId, Guid PublicationId, string State, string? DownloadPath,
    DateTimeOffset? PublishedAt, DateTimeOffset? ExpiresAt);

/// <summary>本人条件取消尚未提交发布意图的导出。</summary>
/// <param name="ExportId">导出标识。</param>
/// <param name="ExpectedVersion">观察到的导出版本。</param>
public sealed record CancelPricingExport(Guid ExportId, long ExpectedVersion) : ICommand<PricingExportStatus>;

/// <summary>后台领取一个到期导出；执行协议不映射到 HTTP。</summary>
[CommandObservationSuppression("轮询只协调有限租约，实际生成使用任务观察。")]
public sealed record ClaimPricingExport : ICommand<PricingExportLease?>;

/// <summary>导出的一次有限执行权；接管更换代次，暂存身份保持不变。</summary>
/// <param name="ExportId">导出标识。</param>
/// <param name="Epoch">执行代次。</param>
/// <param name="UploadId">原委托候选身份；跨接管和本人恢复保持不变。</param>
/// <param name="LeaseUntil">当前租约期限。</param>
/// <param name="MaxLeaseUntil">本次领取总期限。</param>
public sealed record PricingExportLease(Guid ExportId, long Epoch, Guid UploadId, DateTimeOffset LeaseUntil, DateTimeOffset MaxLeaseUntil);

/// <summary>仅在当前代次和租约仍有效时续租；不越过固定总期限。</summary>
/// <param name="ExportId">导出标识。</param>
/// <param name="Epoch">当前执行代次。</param>
[CommandObservationSuppression("续租只协调执行权，实际生成有独立任务观察。")]
public sealed record RenewPricingExport(Guid ExportId, long Epoch) : ICommand<PricingExportLease>;

/// <summary>当前执行者条件提交唯一发布意图；只接纳与冻结描述完全一致的封存回执。</summary>
/// <param name="ExportId">导出标识。</param>
/// <param name="Epoch">当前生成代次。</param>
/// <param name="PublicationId">拟提交并在重试间保留的发布身份。</param>
/// <param name="Receipt">受认证 Files 返回的封存裁决。</param>
[CommandObservationSuppression("发布选择属于实际生成尝试，已有任务观察。")]
public sealed record SelectPricingExportPublication(Guid ExportId, long Epoch, Guid PublicationId,
    GeneratedFileReceiptV1 Receipt) : ICommand<PricingExportStatus>;

/// <summary>领取一条持久发布意图；没有生成权或更换成果的能力。</summary>
[CommandObservationSuppression("轮询只协调发布租约，实际交付有恢复观察。")]
public sealed record ClaimPricingExportPublication : ICommand<PricingExportPublicationLease?>;

/// <summary>固定发布意图的一次有限交付权。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="PublicationId">原发布身份。</param>
/// <param name="Epoch">单调交付代次，与生成代次分开。</param>
/// <param name="LeaseUntil">当前期限。</param>
/// <param name="MaxLeaseUntil">本次总期限。</param>
public sealed record PricingExportPublicationLease(Guid ExportId, Guid PublicationId, long Epoch, DateTimeOffset LeaseUntil, DateTimeOffset MaxLeaseUntil);

/// <summary>完整验证回执后，在当前交付权下原子登记成功。</summary>
/// <param name="Lease">原发布意图的执行权。</param>
/// <param name="Receipt">受认证 Files 的完整发布裁决。</param>
[CommandObservationSuppression("属于实际发布交付的本地提交，已有恢复观察。")]
public sealed record CompletePricingExportPublication(PricingExportPublicationLease Lease, GeneratedFilePublicationReceiptV1 Receipt) : ICommand<PricingExportStatus>;

/// <summary>当前生成执行者把持久快照写成固定 CSV 并提交发布意图。</summary>
/// <param name="Lease">当前有限生成权。</param>
[CommandObservationSuppression("完整生成尝试由后台任务观察统一记录。")]
public sealed record GeneratePricingExport(PricingExportLease Lease) : ICommand<PricingExportStatus>;

/// <summary>恢复原发布裁决并提交本地成功历史，不重新生成。</summary>
/// <param name="Lease">原意图的当前交付权。</param>
[CommandObservationSuppression("完整发布尝试由后台恢复观察统一记录。")]
public sealed record PublishPricingExport(PricingExportPublicationLease Lease) : ICommand<PricingExportStatus>;

/// <summary>登记生成失败；错误码只接受模块定义的有限集合。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="Epoch">当前生成代次。</param>
/// <param name="ErrorCode">稳定失败码。</param>
[CommandObservationSuppression("失败属于已有生成尝试；不另造一个用户操作。")]
public sealed record FailPricingExport(Guid ExportId, long Epoch, string ErrorCode) : ICommand<bool>;

/// <summary>登记发布失败并等待原意图重试；永不重新生成文件。</summary>
/// <param name="Lease">当前发布权。</param>
/// <param name="ErrorCode">稳定失败码。</param>
[CommandObservationSuppression("失败属于已有发布尝试；不另造一个用户操作。")]
public sealed record FailPricingExportPublication(PricingExportPublicationLease Lease, string ErrorCode) : ICommand<bool>;

/// <summary>延长仍有效的发布权，总期限不变。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="PublicationId">原意图。</param>
/// <param name="Epoch">当前交付代次。</param>
[CommandObservationSuppression("发布续租只协调执行权。")]
public sealed record RenewPricingExportPublication(Guid ExportId, Guid PublicationId, long Epoch) : ICommand<PricingExportPublicationLease>;

/// <summary>本人恢复已停机的原委托；条件版本阻止重复重置预算。</summary>
/// <param name="ExportId">原导出。</param>
/// <param name="ExpectedVersion">观察到的停机版本。</param>
[BackgroundWorkAcceptance]
public sealed record RetryPricingExport(Guid ExportId, long ExpectedVersion) : ICommand<PricingExportStatus>;

/// <summary>本人导出的可观察状态；历史成果不等于当前下载可用性。</summary>
/// <param name="ExportId">稳定导出标识。</param>
/// <param name="RequestId">原请求标识。</param>
/// <param name="Version">导出聚合版本。</param>
/// <param name="State">执行阶段。</param>
/// <param name="AcceptedAt">首次数据库接受时刻。</param>
/// <param name="FrozenAt">一致报价快照的数据库观察时刻。</param>
/// <param name="RowCount">原快照行数。</param>
/// <param name="RequestDigest">版本 1 规范化请求摘要。</param>
/// <param name="SnapshotDigest">版本 1 固定 CSV 快照摘要。</param>
public sealed record PricingExportStatus(Guid ExportId, Guid RequestId, long Version, string State,
    DateTimeOffset AcceptedAt, DateTimeOffset FrozenAt, int RowCount, string RequestDigest, string SnapshotDigest)
{
    /// <summary>原行审计信息。</summary>
    public EntityAuditMetadata? Audit { get; init; }
    /// <summary>成功领取的单调执行代次。</summary>
    public long Epoch { get; init; }
    /// <summary>本预算内的领取次数。</summary>
    public int Attempts { get; init; }
    /// <summary>尚未发布阶段的有限本人恢复次数。</summary>
    public long RetryRevision { get; init; }
    /// <summary>当前有限执行权的期限。</summary>
    public DateTimeOffset? LeaseUntil { get; init; }
    /// <summary>本次领取的总期限。</summary>
    public DateTimeOffset? MaxLeaseUntil { get; init; }
    /// <summary>最近稳定错误码。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>已唯一选定的成果引用；发布历史仍需与当前可用性区分。</summary>
    public long? FileId { get; init; }
    /// <summary>本地持久发布意图的稳定身份。</summary>
    public Guid? PublicationId { get; init; }
    /// <summary>首次发布裁决时间。</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>原下载截止时间。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>独立发布交付状态；停止仍保留原 Publishing 意图。</summary>
    public PricingExportPublicationStatus? Delivery { get; init; }
    /// <summary>固定格式与列集版本均为一。</summary>
    public int FormatVersion { get; init; } = 1;
    /// <summary>固定列集版本。</summary>
    public int ColumnSetVersion { get; init; } = 1;
    /// <summary>请求摘要规则版本。</summary>
    public int RequestDigestVersion { get; init; } = 1;
    /// <summary>快照摘要规则版本。</summary>
    public int SnapshotDigestVersion { get; init; } = 1;
    /// <summary>冻结 CSV 的精确实际字节数。</summary>
    public long SnapshotLength { get; init; }
    /// <summary>已封存且被本委托选定的行数；生成中的局部进度不作承诺。</summary>
    public int ConfirmedGeneratedRows => FileId is null ? 0 : RowCount;
}

/// <summary>原发布意图的有限执行与恢复状态，不含成果字节。</summary>
/// <param name="State">Pending、Delivering、Stopped 或 Delivered。</param>
/// <param name="Epoch">交付代次。</param>
/// <param name="Attempts">本轮自动领取次数。</param>
/// <param name="RetryRevision">本人恢复次数。</param>
/// <param name="LeaseUntil">当前期限。</param>
/// <param name="MaxLeaseUntil">本轮固定总期限。</param>
/// <param name="StoppedAt">当前停止时间。</param>
/// <param name="CompletedAt">本地成功登记时间。</param>
/// <param name="ErrorCode">稳定交付错误。</param>
public sealed record PricingExportPublicationStatus(string State, long Epoch, int Attempts, long RetryRevision,
    DateTimeOffset? LeaseUntil, DateTimeOffset? MaxLeaseUntil, DateTimeOffset? StoppedAt, DateTimeOffset? CompletedAt, string? ErrorCode);
