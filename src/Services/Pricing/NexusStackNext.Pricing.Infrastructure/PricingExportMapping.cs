using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal static class PricingExportMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    internal static void ConfigurePricingExports(this ModelBuilder model)
    {
        var export = model.Entity<PricingExport>();
        export.ToTable("exports");
        export.HasKey(x => x.Id);
        export.Property(x => x.Id).HasConversion(x => x.Value, x => new PricingExportId(x)).ValueGeneratedNever();
        export.Property(x => x.OwnerId).HasMaxLength(200);
        export.Property(x => x.CanonicalRequest).HasMaxLength(262_144);
        export.Property(x => x.RequestDigest).HasMaxLength(64);
        export.Property(x => x.SnapshotDigest).HasMaxLength(64);
        export.Property(x => x.State).HasMaxLength(24);
        export.Property(x => x.ErrorCode).HasMaxLength(100);
        export.Property(x => x.Producer).HasMaxLength(32);
        // 回执是外部协议历史，不能由本库 timestamp 的微秒精度截掉原裁决的 100ns 尾数。
        // UTC ticks 同时保留精确重放和可排序性；本地裁决/租约仍使用 PostgreSQL 时间列。
        var receiptTime = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        export.Property(x => x.PublishedAt).HasConversion(receiptTime);
        export.Property(x => x.ExpiresAt).HasConversion(receiptTime);
        export.HasIndex(x => x.PublicationId).IsUnique().HasFilter("\"PublicationId\" IS NOT NULL");
        export.Property(x => x.Version).IsConcurrencyToken();
        export.Property(x => x.RowCount).HasComputedColumnSql("jsonb_array_length(\"Rows\")", stored: true);
        export.Ignore(x => x.DomainEvents);
        export.Property<ExecutionOrigin?>(PricingExportCommands.OriginProperty).HasColumnType("jsonb").HasConversion(
            value => JsonSerializer.Serialize(value, Json), value => JsonSerializer.Deserialize<ExecutionOrigin>(value, Json));
        export.HasIndex(x => new { x.OwnerId, x.RequestId }).IsUnique();
        export.HasIndex(x => new { x.OwnerId, x.AcceptedAt, x.Id });
        export.HasIndex(x => new { x.AvailableAt, x.AcceptedAt, x.Id }).HasFilter("\"State\" = 'Queued'");
        export.HasIndex(x => new { x.LeaseUntil, x.AcceptedAt, x.Id }).HasFilter("\"State\" = 'Generating'");
        export.Property(x => x.Rows).HasColumnType("jsonb")
            .HasConversion(value => JsonSerializer.Serialize(value, Json),
                value => Array.AsReadOnly(JsonSerializer.Deserialize<PricingExportRow[]>(value, Json)!))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<PricingExportRow>>(
                (left, right) => left!.SequenceEqual(right!),
                value => value.Aggregate(0, (hash, row) => HashCode.Combine(hash, row)),
                value => Array.AsReadOnly(value.ToArray())));

        var publication = model.Entity<PricingExportPublication>();
        publication.ToTable("export_publications");
        publication.HasKey(x => x.PublicationId);
        publication.Property(x => x.PublicationId).ValueGeneratedNever();
        publication.Property(x => x.ExportId).HasConversion(x => x.Value, x => new PricingExportId(x));
        publication.HasIndex(x => x.ExportId).IsUnique();
        publication.HasOne<PricingExport>().WithOne().HasForeignKey<PricingExportPublication>(x => x.ExportId).OnDelete(DeleteBehavior.Restrict);
        publication.Property(x => x.Producer).HasMaxLength(32);
        publication.Property(x => x.State).HasMaxLength(16);
        publication.Property(x => x.ErrorCode).HasMaxLength(100);
        publication.HasIndex(x => new { x.State, x.AvailableAt, x.PublicationId });
    }
}

// 发布 Outbox 是 Export 的交付元数据：只有稳定引用和投递状态，不含报价快照或文件字节。
internal sealed class PricingExportPublication
{
    public Guid PublicationId { get; set; }
    public PricingExportId ExportId { get; set; } = null!;
    public Guid UploadId { get; set; }
    public long FileId { get; set; }
    public string Producer { get; set; } = "pricing";
    public DateTimeOffset SelectedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public string State { get; set; } = "Pending";
    public long Epoch { get; set; }
    public int Attempts { get; set; }
    public long RetryRevision { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? MaxLeaseUntil { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
}
