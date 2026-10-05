using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

internal sealed class FactPolicyControl
{
    public int Id { get; set; }
    public long PolicyRevision { get; set; }
    public long MaxRecords { get; set; }
    public long MaxPayloadBytes { get; set; }
    public int MaxRecordPayloadBytes { get; set; }
    public long RetainedRecords { get; set; }
    public long RetainedPayloadBytes { get; set; }
}

internal sealed class FactPolicyReceiptRecord
{
    public Guid RequestId { get; set; }
    public required string RecordJson { get; set; }
    public int PayloadBytes { get; set; }
    public Guid? EventId { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

/// <summary>所属上下文控制账本与不可变凭据的共同模型，表仍落在该上下文 schema。</summary>
public static class FactCapacityPolicyModel
{
    /// <summary>在调用上下文自己的默认 schema 创建有限控制模型。</summary>
    /// <param name="modelBuilder">所属上下文模型。</param>
    public static void ConfigureFactCapacityPolicy(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var control = modelBuilder.Entity<FactPolicyControl>();
        control.ToTable("fact_policy_control", table =>
        {
            table.HasCheckConstraint("ck_fact_policy_control_singleton", "\"Id\" = 1");
            table.HasCheckConstraint("ck_fact_policy_control_bounds", "\"PolicyRevision\" > 0 AND \"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\" AND \"RetainedRecords\" >= 0 AND \"RetainedRecords\" <= \"MaxRecords\" AND \"RetainedPayloadBytes\" >= 0 AND \"RetainedPayloadBytes\" <= \"MaxPayloadBytes\"");
        });
        control.HasKey(item => item.Id);
        control.Property(item => item.Id).ValueGeneratedNever();
        control.HasData(new FactPolicyControl
        {
            Id = 1,
            PolicyRevision = 1,
            MaxRecords = 1000,
            MaxPayloadBytes = 16 * 1024 * 1024,
            MaxRecordPayloadBytes = 16 * 1024,
        });
        var receipt = modelBuilder.Entity<FactPolicyReceiptRecord>();
        receipt.ToTable("fact_policy_receipts", table => table.HasCheckConstraint("ck_fact_policy_receipt_bytes", "\"PayloadBytes\" > 0"));
        receipt.HasKey(item => item.RequestId);
        receipt.Property(item => item.RequestId).ValueGeneratedNever();
        receipt.Property(item => item.RecordJson).IsRequired();
        receipt.HasIndex(item => item.EventId).IsUnique();
        receipt.HasIndex(item => new { item.RetainUntil, item.RequestId });
        receipt.HasOne<OutboxEntry>().WithMany().HasForeignKey(item => item.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
