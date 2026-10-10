using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Domain.Exports;

/// <summary>一次调查导出的稳定身份。</summary>
public sealed record AuditExportId : StronglyTypedId<Guid>
{
    /// <summary>使用调用方提供的身份。</summary>
    /// <param name="value">非空 UUID。</param>
    public AuditExportId(Guid value) : base(value) { }
}

/// <summary>本人已经接受的调查快照；不拥有来源业务对象。</summary>
public sealed class AuditExport : AuditedAggregateRoot<AuditExportId>
{
    private AuditExport(AuditExportId id) : base(id) { }
    /// <summary>原申请人。</summary>
    public string OwnerId { get; private set; } = string.Empty;
    /// <summary>本人范围内的幂等身份。</summary>
    public Guid RequestId { get; private set; }
    /// <summary>原始规范化筛选，不因默认窗口随时间变化。</summary>
    public string CanonicalRequest { get; private set; } = string.Empty;
    /// <summary>facts 或 operations；两类证据不混同。</summary>
    public string Kind { get; private set; } = string.Empty;
    /// <summary>接受时刻。</summary>
    public DateTimeOffset AcceptedAt { get; private set; }
    /// <summary>开始一致观察的时刻。</summary>
    public DateTimeOffset FrozenAt { get; private set; }
    /// <summary>首次确定的来源时间下界。</summary>
    public DateTimeOffset From { get; private set; }
    /// <summary>首次确定的来源时间上界。</summary>
    public DateTimeOffset To { get; private set; }
    /// <summary>不可变的白名单精确文本行。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; private set; } = [];
    /// <summary>冻结行数。</summary>
    public int RowCount { get; private set; }
    /// <summary>异步处理状态。</summary>
    public string State { get; private set; } = "Queued";
    /// <summary>单调执行代次。</summary>
    public long Epoch { get; private set; }
    /// <summary>当前自动预算已领取次数。</summary>
    public int Attempts { get; private set; }
    /// <summary>下次可领取时间。</summary>
    public DateTimeOffset ReadyAt { get; private set; }
    /// <summary>有限执行权截止时间。</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }
    /// <summary>已选定的原成果；非空后不能取消或重新生成。</summary>
    public long? FileId { get; private set; }
    /// <summary>已选定字节摘要。</summary>
    public string? ArtifactDigest { get; private set; }
    /// <summary>原字节长度。</summary>
    public long? ArtifactLength { get; private set; }
    /// <summary>外部原发布时刻，精确保留 UTC ticks。</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>外部原下载截止时间。</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }
    /// <summary>最近白名单错误码。</summary>
    public string? ErrorCode { get; private set; }
    /// <summary>本人条件恢复的次数。</summary>
    public int RetryRevision { get; private set; }

    /// <summary>接受一份有界快照；身份和时刻均由调用方提供。</summary>
    /// <param name="id">原导出身份。</param>
    /// <param name="ownerId">已认证的申请人。</param>
    /// <param name="requestId">原请求身份。</param>
    /// <param name="canonicalRequest">规范化请求。</param>
    /// <param name="kind">证据类别。</param>
    /// <param name="now">接受时刻。</param>
    /// <param name="frozenAt">一致观察开始时刻。</param>
    /// <param name="from">来源时间下界。</param>
    /// <param name="to">来源时间上界。</param>
    /// <param name="rows">最多五千行白名单精确文本。</param>
    /// <returns>原快照或明确的参数错误。</returns>
    public static Result<AuditExport> Accept(AuditExportId id, string ownerId, Guid requestId, string canonicalRequest,
        string kind, DateTimeOffset now, DateTimeOffset frozenAt, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128 || requestId == Guid.Empty
            || string.IsNullOrWhiteSpace(canonicalRequest) || canonicalRequest.Length > 16_384 || kind is not ("facts" or "operations")
            || from > to || to - from > TimeSpan.FromDays(31) || rows.Count is < 1 or > 5000)
        { return Result.Failure<AuditExport>(new Error("auditing.export.invalid", "调查导出的身份、筛选或行数无效。")); }
        return Result.Success(new AuditExport(id)
        {
            OwnerId = ownerId,
            RequestId = requestId,
            CanonicalRequest = canonicalRequest,
            Kind = kind,
            AcceptedAt = now,
            FrozenAt = frozenAt,
            From = from,
            To = to,
            ReadyAt = now,
            Rows = Array.AsReadOnly(rows.Select(static row => (IReadOnlyList<string>)Array.AsReadOnly(row.ToArray())).ToArray()),
            RowCount = rows.Count,
        });
    }

    /// <summary>取消尚未提交发布意图的本人委托；重放取消不推进版本。</summary>
    /// <param name="expectedVersion">调用方观察到的版本。</param>
    /// <returns>取消结果。</returns>
    public Result Cancel(long expectedVersion)
    {
        if (State == "Cancelled") { return Result.Success(); }
        if (expectedVersion != Version || State is not ("Queued" or "Generating"))
        { return Result.Failure(new Error("auditing.export.cancel_conflict", "导出状态已经变化或已提交发布意图。")); }
        State = "Cancelled";
        LeaseUntil = null;
        Rows = [];
        return Changed();
    }

    /// <summary>领取原委托；接管只改变代次，不更换候选或快照。</summary>
    /// <param name="now">数据库时刻。</param>
    /// <param name="duration">本次固定执行期限。</param>
    /// <param name="maxAttempts">有限自动预算。</param>
    /// <returns>是否取得生成或发布执行权。</returns>
    public Result<bool> TryClaim(DateTimeOffset now, TimeSpan duration, int maxAttempts)
    {
        if (State is not ("Queued" or "Generating" or "Publishing") || ReadyAt > now || LeaseUntil > now) { return Result.Success(false); }
        if (Attempts >= maxAttempts)
        {
            State = "Failed"; LeaseUntil = null; ErrorCode ??= "auditing.export.attempts_exhausted";
            return Changed(false);
        }
        State = FileId is null ? "Generating" : "Publishing";
        Epoch++; Attempts++; LeaseUntil = now + duration; ErrorCode = null;
        return Changed(true);
    }

    /// <summary>在当前生成权下提交唯一发布意图。</summary>
    /// <param name="epoch">当前代次。</param>
    /// <param name="now">数据库时刻。</param>
    /// <param name="fileId">已经完整封存的成果。</param>
    /// <param name="digest">封存摘要。</param>
    /// <param name="length">封存字节数。</param>
    /// <returns>选择结果。</returns>
    public Result SelectPublication(long epoch, DateTimeOffset now, long fileId, string digest, long length)
    {
        if (State != "Generating" || !Owns(epoch, now)) { return Lost(); }
        if (fileId <= 0 || length is < 1 or > 33_554_432 || digest.Length != 64 || !digest.All(char.IsAsciiHexDigitLower))
        { return Result.Failure(new Error("auditing.export.invalid_receipt", "原成果回执无效。")); }
        FileId = fileId; ArtifactDigest = digest; ArtifactLength = length; State = "Publishing";
        return Changed();
    }

    /// <summary>登记原发布历史，当前可下载性仍由 Files 裁决。</summary>
    /// <param name="epoch">当前代次。</param>
    /// <param name="now">数据库时刻。</param>
    /// <param name="publishedAt">原发布时刻。</param>
    /// <param name="expiresAt">原截止时间。</param>
    /// <returns>登记结果。</returns>
    public Result Complete(long epoch, DateTimeOffset now, DateTimeOffset publishedAt, DateTimeOffset expiresAt)
    {
        if (State != "Publishing" || !Owns(epoch, now)) { return Lost(); }
        if (publishedAt == default || expiresAt <= publishedAt) { return Result.Failure(new Error("auditing.export.invalid_receipt", "原发布时刻无效。")); }
        State = "Succeeded"; PublishedAt = publishedAt; ExpiresAt = expiresAt; LeaseUntil = null; ErrorCode = null; Rows = [];
        return Changed();
    }

    /// <summary>当前尝试失败后等待原身份重试；耗尽预算明确停机。</summary>
    /// <param name="epoch">当前代次。</param>
    /// <param name="now">数据库时刻。</param>
    /// <param name="code">所属模块的安全错误码。</param>
    /// <param name="maxAttempts">自动预算。</param>
    /// <returns>是否登记当前失败。</returns>
    public Result Fail(long epoch, DateTimeOffset now, string code, int maxAttempts)
    {
        if (State is not ("Generating" or "Publishing") || !Owns(epoch, now)) { return Lost(); }
        ErrorCode = code; LeaseUntil = null; ReadyAt = now.AddSeconds(1);
        State = Attempts >= maxAttempts ? "Failed" : FileId is null ? "Queued" : "Publishing";
        return Changed();
    }

    /// <summary>本人有条件恢复停机委托，仍使用原快照和成果身份。</summary>
    /// <param name="expectedVersion">观察版本。</param>
    /// <param name="now">数据库时刻。</param>
    /// <returns>恢复结果。</returns>
    public Result Retry(long expectedVersion, DateTimeOffset now)
    {
        if (State != "Failed" || Version != expectedVersion || RetryRevision >= 10 || (FileId is null && now >= AcceptedAt.AddDays(7)))
        { return Result.Failure(new Error("auditing.export.retry_conflict", "原委托不能恢复或版本已变化。")); }
        RetryRevision++; Attempts = 0; LeaseUntil = null; ReadyAt = now; ErrorCode = null;
        State = FileId is null ? "Queued" : "Publishing";
        return Changed();
    }

    /// <summary>七天后释放未生成快照，保留原请求及已发布历史。</summary>
    /// <param name="now">数据库时刻。</param>
    /// <returns>清理结果。</returns>
    public Result ExpireSnapshot(DateTimeOffset now)
    {
        if (now < AcceptedAt.AddDays(7) || Rows.Count == 0) { return Result.Success(); }
        Rows = [];
        if (FileId is null && State is not ("Succeeded" or "Cancelled")) { State = "Expired"; LeaseUntil = null; }
        return Changed();
    }

    private bool Owns(long epoch, DateTimeOffset now) => epoch == Epoch && LeaseUntil > now;
    private static Result Lost() => Result.Failure(new Error("auditing.export.lease_lost", "原执行权已经结束。"));
}
