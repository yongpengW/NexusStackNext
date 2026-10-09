using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Pricing.Domain;

/// <summary>导出自身的稳定标识，与调用方请求标识分开。</summary>
public sealed record PricingExportId : StronglyTypedId<Guid>
{
    /// <summary>构造非空标识。</summary>
    /// <param name="value">调用方提供的标识。</param>
    public PricingExportId(Guid value) : base(value) { }
}

/// <summary>一份不可变报价快照及其异步成果委托；不拥有或修改报价聚合。</summary>
public sealed class PricingExport : AuditedAggregateRoot<PricingExportId>
{
    private PricingExport(PricingExportId id) : base(id) { }

    /// <summary>首次接受时固定的精确归属。</summary>
    public string OwnerId { get; private set; } = string.Empty;
    /// <summary>归属范围内的原请求标识。</summary>
    public Guid RequestId { get; private set; }
    /// <summary>版本化规范化请求，保留完整内容用于冲突判断。</summary>
    public string CanonicalRequest { get; private set; } = string.Empty;
    /// <summary>规范化请求摘要。</summary>
    public string RequestDigest { get; private set; } = string.Empty;
    /// <summary>请求摘要规则版本。</summary>
    public int RequestDigestVersion { get; private set; } = 1;
    /// <summary>精确固定 CSV 快照摘要。</summary>
    public string SnapshotDigest { get; private set; } = string.Empty;
    /// <summary>固定 CSV 的精确实际字节数。</summary>
    public long SnapshotLength { get; private set; }
    /// <summary>首次请求选定的输出格式，不改变规范化快照摘要。</summary>
    public string Format { get; private set; } = "csv";
    /// <summary>唯一选定文件的实际摘要；不假设 XLSX ZIP 等于快照文本。</summary>
    public string? ArtifactDigest { get; private set; }
    /// <summary>唯一选定文件的实际字节数。</summary>
    public long? ArtifactLength { get; private set; }
    /// <summary>快照摘要规则版本。</summary>
    public int SnapshotDigestVersion { get; private set; } = 1;
    /// <summary>首次数据库接受时刻。</summary>
    public DateTimeOffset AcceptedAt { get; private set; }
    /// <summary>一致数据库观察时刻。</summary>
    public DateTimeOffset FrozenAt { get; private set; }
    /// <summary>不可变有序报价值；不引用活报价。</summary>
    public IReadOnlyList<PricingExportRow> Rows { get; private set; } = [];
    /// <summary>原快照行数。</summary>
    public int RowCount { get; private set; }
    /// <summary>异步执行阶段。</summary>
    public string State { get; private set; } = "Queued";
    /// <summary>单调执行代次。</summary>
    public long Epoch { get; private set; }
    /// <summary>当前自动预算内的领取次数。</summary>
    public int Attempts { get; private set; }
    /// <summary>有限本人恢复次数，不重新冻结快照或更换候选。</summary>
    public long RetryRevision { get; private set; }
    /// <summary>下一次可领取时间。</summary>
    public DateTimeOffset AvailableAt { get; private set; }
    /// <summary>当前有效执行权的期限。</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }
    /// <summary>本次领取的固定最长期限。</summary>
    public DateTimeOffset? MaxLeaseUntil { get; private set; }
    /// <summary>导出的固定候选身份，接管和未知上传恢复不更换；选定前不代表成果。</summary>
    public Guid? UploadId { get; private set; }
    /// <summary>最近稳定失败码。</summary>
    public string? ErrorCode { get; private set; }
    /// <summary>已选定候选的文件引用。</summary>
    public long? FileId { get; private set; }
    /// <summary>首次选定的发布身份。</summary>
    public Guid? PublicationId { get; private set; }
    /// <summary>选定候选持久保存的证书生产者。</summary>
    public string? Producer { get; private set; }
    /// <summary>首次发布意图的数据库提交裁决时刻。</summary>
    public DateTimeOffset? PublicationSelectedAt { get; private set; }
    /// <summary>Files 首次提交的发布历史时间；当前不可下载不会覆盖它。</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>Files 首次裁决的下载截止时间；重放不延长。</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>接受一份已经冻结的有界快照；时间、标识和摘要均由调用方明确提供。</summary>
    /// <param name="id">导出标识。</param>
    /// <param name="ownerId">可信归属。</param>
    /// <param name="requestId">非空请求标识。</param>
    /// <param name="canonicalRequest">版本 1 规范化请求。</param>
    /// <param name="requestDigest">请求摘要。</param>
    /// <param name="snapshotDigest">固定 CSV 摘要。</param>
    /// <param name="snapshotLength">固定 CSV 的精确字节数。</param>
    /// <param name="acceptedAt">数据库接受时间。</param>
    /// <param name="frozenAt">数据库观察时间。</param>
    /// <param name="rows">最多五千行的非空快照。</param>
    /// <param name="format">csv 或 xlsx；旧调用保留 CSV。</param>
    /// <returns>导出或稳定拒绝。</returns>
    public static Result<PricingExport> Accept(PricingExportId id, string ownerId, Guid requestId,
        string canonicalRequest, string requestDigest, string snapshotDigest, long snapshotLength, DateTimeOffset acceptedAt,
        DateTimeOffset frozenAt, IReadOnlyList<PricingExportRow> rows, string format = "csv")
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Value == Guid.Empty || string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 200 || ownerId.Any(char.IsControl)
            || requestId == Guid.Empty || string.IsNullOrEmpty(canonicalRequest) || canonicalRequest.Length > 262_144
            || !IsDigest(requestDigest) || !IsDigest(snapshotDigest) || snapshotLength is < 1 or > 33_554_432
            || rows is null || rows.Count is < 1 or > 5000
            || frozenAt == default || acceptedAt < frozenAt || format is not ("csv" or "xlsx"))
        { return Result.Failure<PricingExport>(new Error("pricing.export.invalid", "导出身份或快照无效。")); }
        return Result.Success(new PricingExport(id)
        {
            OwnerId = ownerId,
            RequestId = requestId,
            CanonicalRequest = canonicalRequest,
            RequestDigest = requestDigest,
            SnapshotDigest = snapshotDigest,
            SnapshotLength = snapshotLength,
            Format = format,
            AcceptedAt = acceptedAt,
            FrozenAt = frozenAt,
            AvailableAt = acceptedAt,
            Rows = Array.AsReadOnly(rows.ToArray()),
            RowCount = rows.Count,
        });
    }

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>取消尚未开始发布的委托；终态重放不推进版本。</summary>
    /// <param name="expectedVersion">调用方观察到的版本。</param>
    /// <returns>取消或过期观察拒绝。</returns>
    public Result Cancel(long expectedVersion)
    {
        if (State == "Canceled") { return Result.Success(); }
        if (Version != expectedVersion || State is not ("Queued" or "Generating"))
        { return Result.Failure(new Error("pricing.export.cancel_conflict", "导出状态已改变，不能按原观察取消。")); }
        State = "Canceled";
        LeaseUntil = null;
        return Changed();
    }

    /// <summary>领取或接管已到期执行；未到期、终态和预算耗尽均不能获得执行权。</summary>
    /// <param name="now">数据库当前时间。</param>
    /// <param name="uploadId">首次领取由调用方提供的候选身份；已有身份保持不变。</param>
    /// <param name="leaseDuration">单次租约长度。</param>
    /// <param name="maxDuration">本次固定总期限。</param>
    /// <param name="maxAttempts">自动领取预算。</param>
    /// <returns>是否取得执行权。</returns>
    public Result<bool> TryClaim(DateTimeOffset now, Guid uploadId, TimeSpan leaseDuration, TimeSpan maxDuration, int maxAttempts)
    {
        if (State == "Queued" ? AvailableAt > now : State != "Generating" || LeaseUntil > now)
        { return Result.Success(false); }
        if (Attempts >= maxAttempts)
        {
            State = "Failed";
            ErrorCode = "pricing.export.execution_exhausted";
            LeaseUntil = null;
            return Changed(false);
        }
        if (uploadId == Guid.Empty || leaseDuration <= TimeSpan.Zero || maxDuration <= leaseDuration || maxAttempts < 1)
        { return Result.Failure<bool>(new Error("pricing.export.invalid_lease", "导出执行策略无效。")); }
        State = "Generating";
        Epoch++;
        Attempts++;
        UploadId ??= uploadId;
        LeaseUntil = now.Add(leaseDuration);
        MaxLeaseUntil = now.Add(maxDuration);
        ErrorCode = null;
        return Changed(true);
    }

    /// <summary>在仍持有执行权时延长租约；总期限保持首次领取值。</summary>
    /// <param name="epoch">当前执行代次。</param>
    /// <param name="now">数据库时间。</param>
    /// <param name="duration">请求的租约长度。</param>
    /// <returns>是否实际延长；失权不会复活工作。</returns>
    public Result<bool> Renew(long epoch, DateTimeOffset now, TimeSpan duration)
    {
        if (State != "Generating" || Epoch != epoch || LeaseUntil is null || LeaseUntil <= now
            || MaxLeaseUntil is null || MaxLeaseUntil <= now)
        { return Result.Failure<bool>(new Error("pricing.export.lease_lost", "当前执行权已失效。")); }
        var requested = now.Add(duration);
        var extended = requested < MaxLeaseUntil.Value ? requested : MaxLeaseUntil.Value;
        if (extended <= LeaseUntil.Value) { return Result.Success(false); }
        LeaseUntil = extended;
        return Changed(true);
    }

    /// <summary>当前执行者唯一选定候选；已提交意图不能被另一身份替换。</summary>
    /// <param name="epoch">生成代次。</param>
    /// <param name="now">数据库时间。</param>
    /// <param name="fileId">已完整验证的候选文件。</param>
    /// <param name="publicationId">稳定发布身份。</param>
    /// <param name="producer">已验证的生产者。</param>
    /// <param name="artifactDigest">选定文件的实际摘要；CSV 旧调用可省略。</param>
    /// <param name="artifactLength">选定文件的实际长度；CSV 旧调用可省略。</param>
    /// <returns>是否首次选定。</returns>
    public Result<bool> SelectPublication(long epoch, DateTimeOffset now, long fileId, Guid publicationId, string producer,
        string? artifactDigest = null, long? artifactLength = null)
    {
        artifactDigest ??= Format == "csv" ? SnapshotDigest : null;
        artifactLength ??= Format == "csv" ? SnapshotLength : null;
        if (State is "Publishing" or "Succeeded")
        {
            return Epoch == epoch && FileId == fileId && PublicationId == publicationId && Producer == producer
                && ArtifactDigest == artifactDigest && ArtifactLength == artifactLength
                ? Result.Success(false)
                : Result.Failure<bool>(new Error("pricing.export.selection_conflict", "已有发布意图不能替换。"));
        }
        if (State != "Generating" || Epoch != epoch || LeaseUntil is null || LeaseUntil <= now || MaxLeaseUntil is null || MaxLeaseUntil <= now)
        { return Result.Failure<bool>(new Error("pricing.export.lease_lost", "当前执行权已失效。")); }
        if (fileId <= 0 || publicationId == Guid.Empty || producer != "pricing" || !IsDigest(artifactDigest)
            || artifactLength is null or < 1 or > 33_554_432
            || (Format == "csv" && (artifactDigest != SnapshotDigest || artifactLength != SnapshotLength)))
        { return Result.Failure<bool>(new Error("pricing.export.invalid_receipt", "成果回执无效。")); }
        FileId = fileId;
        PublicationId = publicationId;
        Producer = producer;
        ArtifactDigest = artifactDigest;
        ArtifactLength = artifactLength;
        PublicationSelectedAt = now;
        State = "Publishing";
        LeaseUntil = null;
        ErrorCode = null;
        return Changed(true);
    }

    /// <summary>发布 Outbox 的实际可观察执行状态改变时，推进所属聚合版本。</summary>
    /// <param name="errorCode">经过白名单转换的稳定错误码。</param>
    /// <returns>只有已选定发布意图才能推进交付。</returns>
    public Result RecordPublicationProgress(string? errorCode)
    {
        if (State != "Publishing") { return Result.Failure(new Error("pricing.export.lease_lost", "发布执行权已失效。")); }
        ErrorCode = errorCode;
        return Changed();
    }

    /// <summary>完整回执已与原意图校验后登记成功；相同历史重放不改变版本。</summary>
    /// <param name="publishedAt">原发布时间。</param>
    /// <param name="expiresAt">原下载截止时间。</param>
    /// <returns>成功历史或冲突。</returns>
    public Result CompletePublication(DateTimeOffset publishedAt, DateTimeOffset expiresAt)
    {
        if (State == "Succeeded")
        {
            return PublishedAt == publishedAt && ExpiresAt == expiresAt ? Result.Success()
                : Result.Failure(new Error("pricing.export.invalid_receipt", "发布历史与原裁决不一致。"));
        }
        if (State != "Publishing" || publishedAt == default || expiresAt <= publishedAt)
        { return Result.Failure(new Error("pricing.export.invalid_receipt", "发布裁决无效。")); }
        PublishedAt = publishedAt;
        ExpiresAt = expiresAt;
        State = "Succeeded";
        ErrorCode = null;
        return Changed();
    }

    /// <summary>仅当前生成权能登记失败；自动预算耗尽或候选关闭时停机。</summary>
    /// <param name="epoch">生成代次。</param>
    /// <param name="now">数据库时间。</param>
    /// <param name="errorCode">适配器白名单失败码。</param>
    /// <param name="permanent">原候选或快照已经不能继续。</param>
    /// <param name="maxAttempts">自动预算。</param>
    /// <returns>当前失败登记或失权。</returns>
    public Result FailGeneration(long epoch, DateTimeOffset now, string errorCode, bool permanent, int maxAttempts)
    {
        if (State != "Generating" || Epoch != epoch || LeaseUntil is null || LeaseUntil <= now || MaxLeaseUntil is null || MaxLeaseUntil <= now)
        { return Result.Failure(new Error("pricing.export.lease_lost", "当前生成执行权已失效。")); }
        State = permanent || Attempts >= maxAttempts ? "Failed" : "Queued";
        AvailableAt = now.AddSeconds(Math.Min(30, 1 << Math.Min(Attempts, 4)));
        LeaseUntil = null;
        ErrorCode = errorCode;
        return Changed();
    }

    /// <summary>本人恢复尚未发布的停机生成；保留原快照、候选和单调代次。</summary>
    /// <param name="expectedVersion">停机观察版本。</param>
    /// <param name="now">数据库时间。</param>
    /// <returns>重新开放有限自动预算或冲突。</returns>
    public Result RetryGeneration(long expectedVersion, DateTimeOffset now)
    {
        if (State != "Failed" || Version != expectedVersion || RetryRevision >= 10
            || ErrorCode is "pricing.export.file_closed" or "pricing.export.snapshot_corrupt" or "pricing.export.candidate_bytes_conflict")
        { return Result.Failure(new Error("pricing.export.retry_conflict", "当前停机状态不能按原观察恢复。")); }
        State = "Queued";
        RetryRevision++;
        Attempts = 0;
        AvailableAt = now;
        LeaseUntil = null;
        MaxLeaseUntil = null;
        ErrorCode = null;
        return Changed();
    }
}
