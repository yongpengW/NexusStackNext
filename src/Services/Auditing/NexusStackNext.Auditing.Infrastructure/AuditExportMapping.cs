using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NexusStackNext.Auditing.Domain.Exports;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Auditing.Infrastructure;

internal static class AuditExportMapping
{
    internal static void ConfigureAuditExports(this ModelBuilder model)
    {
        var export = model.Entity<AuditExport>();
        export.ToTable("exports");
        export.HasKey(x => x.Id);
        export.Property(x => x.Id).HasConversion(x => x.Value, x => new AuditExportId(x)).ValueGeneratedNever();
        export.Property(x => x.OwnerId).HasMaxLength(128);
        export.Property(x => x.CanonicalRequest).HasMaxLength(16_384);
        export.Property(x => x.Kind).HasMaxLength(16);
        export.Property(x => x.State).HasMaxLength(24);
        export.Property(x => x.ErrorCode).HasMaxLength(100);
        export.Property(x => x.ArtifactDigest).HasMaxLength(64);
        var receiptTime = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        export.Property(x => x.PublishedAt).HasConversion(receiptTime);
        export.Property(x => x.ExpiresAt).HasConversion(receiptTime);
        export.Property<ExecutionOrigin?>("ExecutionOrigin").HasColumnType("jsonb").HasConversion(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null), value => JsonSerializer.Deserialize<ExecutionOrigin>(value, (JsonSerializerOptions?)null));
        export.Property(x => x.Version).IsConcurrencyToken();
        export.Ignore(x => x.DomainEvents);
        export.Property(x => x.Rows).HasColumnType("jsonb").HasConversion(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => ReadRows(value))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<IReadOnlyList<string>>>(
                (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
                value => ReadRows(JsonSerializer.Serialize(value, (JsonSerializerOptions?)null))));
        export.HasIndex(x => new { x.OwnerId, x.RequestId }).IsUnique();
        export.HasIndex(x => new { x.OwnerId, x.AcceptedAt, x.Id });
        export.HasIndex(x => new { x.State, x.ReadyAt, x.LeaseUntil }).HasFilter("\"State\" IN ('Queued', 'Generating', 'Publishing')");
        export.HasIndex(x => new { x.AcceptedAt, x.Id }).HasFilter("jsonb_array_length(\"Rows\") > 0");
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<IReadOnlyList<string>> ReadRows(string json) => Array.AsReadOnly(JsonSerializer.Deserialize<string[][]>(json)!
        .Select(static row => (IReadOnlyList<string>)Array.AsReadOnly(row)).ToArray());
}
