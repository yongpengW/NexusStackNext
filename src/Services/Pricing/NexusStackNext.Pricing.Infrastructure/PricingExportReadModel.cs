using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.Pricing.Application;
using Npgsql;
using NpgsqlTypes;

namespace NexusStackNext.Pricing.Infrastructure;

// 元数据选择明确列出每一列；本人列表与详情绝不读 Rows、CanonicalRequest 或执行来源载荷。
internal static class PricingExportReadModel
{
    internal const string Select = """
        SELECT e."Id" AS "ExportId", e."RequestId", e."Version", e."State", e."AcceptedAt", e."FrozenAt", e."RowCount",
               e."RequestDigest", e."SnapshotDigest", e."RequestDigestVersion", e."SnapshotDigestVersion", e."SnapshotLength",
               e."CreatedAt", e."CreatedBy", e."UpdatedAt", e."UpdatedBy", e."Epoch", e."Attempts", e."RetryRevision",
               e."LeaseUntil", e."MaxLeaseUntil", e."ErrorCode", e."FileId", e."UploadId", e."PublicationId",
               e."PublishedAt" AS "PublishedAtTicks", e."ExpiresAt" AS "ExpiresAtTicks", e."Format", e."ArtifactDigest", e."ArtifactLength",
               p."State" AS "DeliveryState", p."Epoch" AS "DeliveryEpoch", p."Attempts" AS "DeliveryAttempts",
               p."RetryRevision" AS "DeliveryRetryRevision", p."LeaseUntil" AS "DeliveryLeaseUntil", p."MaxLeaseUntil" AS "DeliveryMaxLeaseUntil",
               p."StoppedAt" AS "DeliveryStoppedAt", p."CompletedAt" AS "DeliveryCompletedAt", p."ErrorCode" AS "DeliveryErrorCode"
        FROM pricing.exports e LEFT JOIN pricing.export_publications p ON p."ExportId" = e."Id"
        """;

    internal static Task<PricingExportMetadata?> ReadAsync(PricingDbContext database, Guid exportId, string? owner, CancellationToken token)
        => database.Database.SqlQueryRaw<PricingExportMetadata>(Select + "\nWHERE e.\"Id\" = @id AND (@owner IS NULL OR e.\"OwnerId\" = @owner)",
            new NpgsqlParameter("id", exportId), Parameter("owner", NpgsqlDbType.Text, owner)).SingleOrDefaultAsync(token);

    internal static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    internal static PricingExportPublicationStatus? Delivery(PricingExportPublication? value) => value is null ? null : new(value.State,
        value.Epoch, value.Attempts, value.RetryRevision, value.LeaseUntil, value.MaxLeaseUntil, value.StoppedAt, value.CompletedAt, value.ErrorCode);
}

internal sealed class PricingExportMetadata
{
    public Guid ExportId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
    public int RowCount { get; set; }
    public string RequestDigest { get; set; } = string.Empty;
    public string SnapshotDigest { get; set; } = string.Empty;
    public int RequestDigestVersion { get; set; }
    public int SnapshotDigestVersion { get; set; }
    public long SnapshotLength { get; set; }
    public string Format { get; set; } = "csv";
    public string? ArtifactDigest { get; set; }
    public long? ArtifactLength { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public long Epoch { get; set; }
    public int Attempts { get; set; }
    public long RetryRevision { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? MaxLeaseUntil { get; set; }
    public string? ErrorCode { get; set; }
    public long? FileId { get; set; }
    public Guid? UploadId { get; set; }
    public Guid? PublicationId { get; set; }
    public long? PublishedAtTicks { get; set; }
    public long? ExpiresAtTicks { get; set; }
    public string? DeliveryState { get; set; }
    public long? DeliveryEpoch { get; set; }
    public int? DeliveryAttempts { get; set; }
    public long? DeliveryRetryRevision { get; set; }
    public DateTimeOffset? DeliveryLeaseUntil { get; set; }
    public DateTimeOffset? DeliveryMaxLeaseUntil { get; set; }
    public DateTimeOffset? DeliveryStoppedAt { get; set; }
    public DateTimeOffset? DeliveryCompletedAt { get; set; }
    public string? DeliveryErrorCode { get; set; }

    public PricingExportStatus Status() => new(ExportId, RequestId, Version, State, AcceptedAt, FrozenAt, RowCount, RequestDigest, SnapshotDigest)
    {
        Audit = new EntityAuditMetadata(CreatedAt, CreatedBy, UpdatedAt, UpdatedBy),
        Format = Format,
        ArtifactDigest = ArtifactDigest,
        ArtifactLength = ArtifactLength,
        Epoch = Epoch,
        Attempts = Attempts,
        RetryRevision = RetryRevision,
        LeaseUntil = LeaseUntil,
        MaxLeaseUntil = MaxLeaseUntil,
        ErrorCode = ErrorCode,
        FileId = FileId,
        PublicationId = PublicationId,
        PublishedAt = PublishedAtTicks is { } published ? new DateTimeOffset(published, TimeSpan.Zero) : null,
        ExpiresAt = ExpiresAtTicks is { } expires ? new DateTimeOffset(expires, TimeSpan.Zero) : null,
        RequestDigestVersion = RequestDigestVersion,
        SnapshotDigestVersion = SnapshotDigestVersion,
        SnapshotLength = SnapshotLength,
        Delivery = DeliveryState is null ? null : new(DeliveryState, DeliveryEpoch!.Value, DeliveryAttempts!.Value, DeliveryRetryRevision!.Value,
            DeliveryLeaseUntil, DeliveryMaxLeaseUntil, DeliveryStoppedAt, DeliveryCompletedAt, DeliveryErrorCode),
    };
}
