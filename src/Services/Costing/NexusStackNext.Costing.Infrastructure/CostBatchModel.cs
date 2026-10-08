using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Infrastructure;

// 生命周期记录与输入快照；不是第二个业务聚合。每次行事务至多修改一个 CostSheet。
internal sealed class CostBatchEntry
{
    public Guid BatchId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = "Pending";
    public int TotalRows { get; set; }
    public int Checkpoint { get; set; }
    public int Imported { get; set; }
    public int Unchanged { get; set; }
    public int DuplicateSuperseded { get; set; }
    public int Rejected { get; set; }
    public ExecutionOrigin? ExecutionOrigin { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? MaxLeaseUntil { get; set; }
    public long Epoch { get; set; }
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }

    public CostBatchStatus ToStatus() => new(BatchId, State, CreatedAt, TotalRows, Checkpoint,
        Imported, Unchanged, DuplicateSuperseded, Rejected)
    { Epoch = Epoch, Attempts = Attempts, LeaseUntil = LeaseUntil, MaxLeaseUntil = MaxLeaseUntil, ErrorCode = ErrorCode };
}

internal sealed class CostBatchAttemptEntry
{
    public Guid BatchId { get; set; }
    public long Epoch { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Outcome { get; set; } = "Running";
    public string? ErrorCode { get; set; }
}

internal sealed class CostBatchRowEntry
{
    public Guid BatchId { get; set; }
    public int Sequence { get; set; }
    public int SourceRow { get; set; }
    public Guid ItemId { get; set; }
    public long ExpectedVersion { get; set; }
    public decimal PurchaseCost { get; set; }
    public decimal FreightCost { get; set; }
    public int EffectiveSequence { get; set; }
    public Guid? TaskId { get; set; }
    public string Outcome { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
}

internal static class CostBatchModel
{
    public static void ConfigureCostBatches(this ModelBuilder builder)
    {
        var batch = builder.Entity<CostBatchEntry>();
        batch.ToTable("batches");
        batch.HasKey(x => x.BatchId);
        batch.Property(x => x.ContentHash).HasMaxLength(80).IsRequired();
        batch.Property(x => x.State).HasMaxLength(24).IsRequired();
        batch.Property(x => x.ErrorCode).HasMaxLength(64);
        batch.Property(x => x.ExecutionOrigin).HasColumnType("jsonb").HasConversion(
            origin => JsonSerializer.Serialize(origin, (JsonSerializerOptions?)null),
            json => JsonSerializer.Deserialize<ExecutionOrigin>(json, (JsonSerializerOptions?)null));
        batch.HasIndex(x => new { x.CreatedAt, x.BatchId });
        batch.HasIndex(x => new { x.State, x.AvailableAt });
        var attempt = builder.Entity<CostBatchAttemptEntry>();
        attempt.ToTable("batch_attempts");
        attempt.HasKey(x => new { x.BatchId, x.Epoch });
        attempt.Property(x => x.Outcome).HasMaxLength(24);
        attempt.Property(x => x.ErrorCode).HasMaxLength(64);
        attempt.HasOne<CostBatchEntry>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        var row = builder.Entity<CostBatchRowEntry>();
        row.ToTable("batch_rows");
        row.HasKey(x => new { x.BatchId, x.Sequence });
        row.Property(x => x.PurchaseCost).HasPrecision(18, 4);
        row.Property(x => x.FreightCost).HasPrecision(18, 4);
        row.Property(x => x.Outcome).HasMaxLength(24).IsRequired();
        row.Property(x => x.ErrorCode).HasMaxLength(64);
        row.HasIndex(x => x.TaskId).IsUnique();
        row.HasOne<CostBatchEntry>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
    }
}
