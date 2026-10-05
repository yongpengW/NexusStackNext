using Microsoft.EntityFrameworkCore;

namespace NexusStackNext.Scheduling.Infrastructure.Persistence;

internal sealed class SchedulingAuditRecoveryControl
{
    public int Id { get; set; }
    public long MaxRecords { get; set; }
    public long MaxPayloadBytes { get; set; }
    public int MaxRecordPayloadBytes { get; set; }
    public long RetainedRecords { get; set; }
    public long RetainedPayloadBytes { get; set; }
}

internal sealed class SchedulingAuditRecoveryRecord
{
    public Guid RequestId { get; set; }
    public required string RecordJson { get; set; }
    public int PayloadBytes { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

internal static class SchedulingAuditRecoveryModel
{
    public static void ConfigureSchedulingAuditRecovery(this ModelBuilder modelBuilder)
    {
        var control = modelBuilder.Entity<SchedulingAuditRecoveryControl>();
        control.ToTable("fact_recovery_control", table =>
        {
            table.HasCheckConstraint("ck_fact_recovery_control_singleton", "\"Id\" = 1");
            table.HasCheckConstraint("ck_fact_recovery_control_bounds", "\"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\" AND \"RetainedRecords\" >= 0 AND \"RetainedRecords\" <= \"MaxRecords\" AND \"RetainedPayloadBytes\" >= 0 AND \"RetainedPayloadBytes\" <= \"MaxPayloadBytes\"");
        });
        control.HasKey(item => item.Id);
        control.Property(item => item.Id).ValueGeneratedNever();
        control.HasData(new SchedulingAuditRecoveryControl
        {
            Id = 1,
            MaxRecords = 1000,
            MaxPayloadBytes = 16 * 1024 * 1024,
            MaxRecordPayloadBytes = 16 * 1024,
        });
        var receipt = modelBuilder.Entity<SchedulingAuditRecoveryRecord>();
        receipt.ToTable("fact_recovery_receipts", table =>
            table.HasCheckConstraint("ck_fact_recovery_receipt_bytes", "\"PayloadBytes\" > 0"));
        receipt.HasKey(item => item.RequestId);
        receipt.Property(item => item.RequestId).ValueGeneratedNever();
        receipt.Property(item => item.RecordJson).IsRequired();
        receipt.HasIndex(item => new { item.RetainUntil, item.RequestId });
    }
}
