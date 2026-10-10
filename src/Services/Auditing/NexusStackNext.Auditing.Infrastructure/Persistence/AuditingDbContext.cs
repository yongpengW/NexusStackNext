using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure.Persistence;

/// <summary>审计上下文自己的事务、记录与迁移历史。</summary>
/// <param name="options">数据库选项。</param>
public sealed class AuditingDbContext(DbContextOptions<AuditingDbContext> options) : NexusStackDbContext(options, SchemaName)
{
    /// <summary>独占 schema。</summary>
    public const string SchemaName = "auditing";
    internal const string PayloadHashProperty = "AuditPayloadHash";

    /// <summary>不可变审计记录。</summary>
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    /// <summary>与已提交事实分开保存的不可变执行观察。</summary>
    public DbSet<OperationObservation> OperationObservations => Set<OperationObservation>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InboxMessage>().Property<string?>(PayloadHashProperty).HasMaxLength(64);
        var entry = modelBuilder.Entity<AuditEntry>();
        entry.ToTable("audit_entries", table => table.HasCheckConstraint("auditing_fact_policy_change_valid", """
            ("EventName" <> "Source" || '.fact-capacity-policy-changed.v1' AND "SubjectType" <> 'fact-capacity-policy' AND "PolicyRequestId" IS NULL AND "PolicyRevision" IS NULL AND "PolicyReason" IS NULL
                AND "PolicyPreviousMaxRecords" IS NULL AND "PolicyPreviousMaxPayloadBytes" IS NULL AND "PolicyPreviousMaxRecordPayloadBytes" IS NULL
                AND "PolicyCurrentMaxRecords" IS NULL AND "PolicyCurrentMaxPayloadBytes" IS NULL AND "PolicyCurrentMaxRecordPayloadBytes" IS NULL)
            OR ("PolicyRequestId" IS NOT NULL AND "PolicyRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "PolicyRevision" IS NOT NULL AND "PolicyRevision" > 1 AND "PolicyRevision" = "SubjectVersion"
                AND "PolicyReason" IS NOT NULL AND "PolicyReason" = 'operator-adjustment'
                AND "PolicyPreviousMaxRecords" IS NOT NULL AND "PolicyPreviousMaxRecords" > 0
                AND "PolicyPreviousMaxPayloadBytes" IS NOT NULL AND "PolicyPreviousMaxPayloadBytes" > 0
                AND "PolicyPreviousMaxRecordPayloadBytes" IS NOT NULL AND "PolicyPreviousMaxRecordPayloadBytes" > 0
                AND "PolicyPreviousMaxRecordPayloadBytes" <= "PolicyPreviousMaxPayloadBytes"
                AND "PolicyCurrentMaxRecords" IS NOT NULL AND "PolicyCurrentMaxRecords" > 0
                AND "PolicyCurrentMaxPayloadBytes" IS NOT NULL AND "PolicyCurrentMaxPayloadBytes" > 0
                AND "PolicyCurrentMaxRecordPayloadBytes" IS NOT NULL AND "PolicyCurrentMaxRecordPayloadBytes" > 0
                AND "PolicyCurrentMaxRecordPayloadBytes" <= "PolicyCurrentMaxPayloadBytes"
                AND ("PolicyPreviousMaxRecords" <> "PolicyCurrentMaxRecords"
                    OR "PolicyPreviousMaxPayloadBytes" <> "PolicyCurrentMaxPayloadBytes"
                    OR "PolicyPreviousMaxRecordPayloadBytes" <> "PolicyCurrentMaxRecordPayloadBytes")
                AND "SubjectType" = 'fact-capacity-policy' AND "SubjectId" = "Source"
                AND "Action" = "Source" || '.fact-capacity-policy.changed'
                AND "EventName" = "Source" || '.fact-capacity-policy-changed.v1' AND "ActorId" IS NOT NULL)
            """));
        entry.HasKey(item => item.Id);
        entry.Property(item => item.Id).HasConversion(id => id.Value, value => new AuditEntryId(value));
        entry.Property(item => item.RecordedAt);
        entry.HasIndex(item => new { item.RecordedAt, item.Id }).HasDatabaseName("ix_audit_entries_recorded");
        entry.OwnsOne(item => item.Fact, fact =>
        {
            fact.Property(item => item.MessageId).HasColumnName("MessageId");
            fact.Property(item => item.EventName).HasColumnName("EventName").HasMaxLength(200);
            fact.Property(item => item.Source).HasColumnName("Source").HasMaxLength(64);
            fact.Property(item => item.Action).HasColumnName("Action").HasMaxLength(200);
            fact.Property(item => item.SubjectType).HasColumnName("SubjectType").HasMaxLength(100);
            fact.Property(item => item.SubjectId).HasColumnName("SubjectId").HasMaxLength(200);
            fact.Property(item => item.SubjectVersion).HasColumnName("SubjectVersion");
            fact.Property(item => item.ActorId).HasColumnName("ActorId").HasMaxLength(200);
            fact.Property(item => item.OccurredAt).HasColumnName("OccurredAt");
            fact.Property(item => item.TraceId).HasColumnName("TraceId").HasMaxLength(128);
            fact.Property(item => item.CorrelationId).HasColumnName("CorrelationId").HasMaxLength(128);
            fact.OwnsOne(item => item.CapacityPolicyChange, policy =>
            {
                policy.Property(item => item.RequestId).HasColumnName("PolicyRequestId");
                policy.Property(item => item.PolicyRevision).HasColumnName("PolicyRevision");
                policy.Property(item => item.Reason).HasColumnName("PolicyReason").HasMaxLength(32);
                policy.OwnsOne(item => item.Previous, limits =>
                {
                    limits.Property(item => item.MaxRecords).HasColumnName("PolicyPreviousMaxRecords");
                    limits.Property(item => item.MaxPayloadBytes).HasColumnName("PolicyPreviousMaxPayloadBytes");
                    limits.Property(item => item.MaxRecordPayloadBytes).HasColumnName("PolicyPreviousMaxRecordPayloadBytes");
                });
                policy.OwnsOne(item => item.Current, limits =>
                {
                    limits.Property(item => item.MaxRecords).HasColumnName("PolicyCurrentMaxRecords");
                    limits.Property(item => item.MaxPayloadBytes).HasColumnName("PolicyCurrentMaxPayloadBytes");
                    limits.Property(item => item.MaxRecordPayloadBytes).HasColumnName("PolicyCurrentMaxRecordPayloadBytes");
                });
                policy.Navigation(item => item.Previous).IsRequired();
                policy.Navigation(item => item.Current).IsRequired();
            });
            fact.OwnsOne(item => item.RelatedSubject, related =>
            {
                related.Property(item => item.Context).HasColumnName("RelatedContext").HasMaxLength(64);
                related.Property(item => item.Type).HasColumnName("RelatedSubjectType").HasMaxLength(100);
                related.Property(item => item.Id).HasColumnName("RelatedSubjectId").HasMaxLength(200);
                related.HasIndex(item => new { item.Context, item.Type, item.Id }).HasDatabaseName("ix_audit_entries_related");
            });
            fact.OwnsOne(item => item.Execution, execution =>
            {
                execution.Property(item => item.OperationId).HasColumnName("OperationId");
                execution.Property(item => item.Source).HasColumnName("OperationSource").HasMaxLength(64);
                execution.Property(item => item.RootOperationId).HasColumnName("RootOperationId");
                execution.Property(item => item.RootSource).HasColumnName("RootSource").HasMaxLength(64);
                execution.Property(item => item.InitiatorId).HasColumnName("InitiatorId").HasMaxLength(200);
                execution.HasIndex(item => new { item.OperationId, item.Source }).HasDatabaseName("ix_audit_entries_operation");
                execution.HasIndex(item => new { item.RootOperationId, item.RootSource }).HasDatabaseName("ix_audit_entries_root");
            });
            fact.HasIndex(item => item.OccurredAt).HasDatabaseName("ix_audit_entries_occurred");
            fact.HasIndex(item => new { item.SubjectType, item.SubjectId, item.OccurredAt }).HasDatabaseName("ix_audit_entries_subject");
            fact.HasIndex(item => new { item.ActorId, item.OccurredAt }).HasDatabaseName("ix_audit_entries_actor");
            fact.HasIndex(item => new { item.EventName, item.MessageId }).IsUnique();
        });
        entry.Navigation(item => item.Fact).IsRequired();

        var observation = modelBuilder.Entity<OperationObservation>();
        observation.ToTable("operation_observations");
        observation.HasKey(item => item.Id);
        observation.Property(item => item.Id).HasConversion(id => id.Value, value => new OperationObservationId(value))
            .HasColumnType("uuid").ValueGeneratedNever();
        observation.Property(item => item.RecordedAt).IsRequired();
        observation.HasIndex(item => new { item.RecordedAt, item.Id }).HasDatabaseName("ix_operation_observations_recorded");
        observation.OwnsOne(item => item.Data, data =>
        {
            data.Property(item => item.OperationId).HasConversion(id => id.Value, value => new OperationId(value))
                .HasColumnName("OperationId").HasColumnType("uuid");
            data.Property(item => item.Source).HasColumnName("Source").HasMaxLength(64);
            data.Property(item => item.Kind).HasColumnName("Kind").HasMaxLength(32);
            data.Property(item => item.Phase).HasColumnName("Phase").HasMaxLength(16);
            data.Property(item => item.Outcome).HasColumnName("Outcome").HasMaxLength(32);
            data.Property(item => item.OccurredAt).HasColumnName("OccurredAt");
            data.Property(item => item.ActorId).HasColumnName("ActorId").HasMaxLength(200);
            data.Property(item => item.TraceId).HasColumnName("TraceId").HasMaxLength(128);
            data.Property(item => item.HttpMethod).HasColumnName("HttpMethod").HasMaxLength(16);
            data.Property(item => item.RouteTemplate).HasColumnName("RouteTemplate").HasMaxLength(500);
            data.Property(item => item.StatusCode).HasColumnName("StatusCode");
            data.Property(item => item.DurationMs).HasColumnName("DurationMs");
            data.OwnsOne(item => item.Metadata, metadata =>
            {
                metadata.Property(item => item.Action).HasColumnName("Action").HasMaxLength(200).IsRequired();
                metadata.Property(item => item.ExecutionRole).HasColumnName("ExecutionRole").HasMaxLength(16).IsRequired();
                metadata.Property(item => item.Description).HasColumnName("Description").HasMaxLength(256);
                metadata.Property(item => item.SubjectType).HasColumnName("SubjectType").HasMaxLength(100);
                metadata.Property(item => item.SubjectIdKind).HasColumnName("SubjectIdKind").HasMaxLength(16);
                metadata.Property(item => item.SubjectId).HasColumnName("SubjectId").HasMaxLength(36);
                metadata.Property(item => item.SpanId).HasColumnName("SpanId").HasMaxLength(16);
                metadata.Property(item => item.ParentSpanId).HasColumnName("ParentSpanId").HasMaxLength(16);
                metadata.Property(item => item.CorrelationId).HasColumnName("CorrelationId").HasMaxLength(64);
                metadata.Property(item => item.RootOperationId).HasColumnName("RootOperationId");
                metadata.Property(item => item.RootSource).HasColumnName("RootSource").HasMaxLength(64);
                metadata.Property(item => item.ParentOperationId).HasColumnName("ParentOperationId");
                metadata.Property(item => item.ParentSource).HasColumnName("ParentSource").HasMaxLength(64);
                metadata.Property(item => item.InitiatorId).HasColumnName("InitiatorId").HasMaxLength(200);
                metadata.Property(item => item.TaskId).HasColumnName("TaskId");
                metadata.Property(item => item.TaskEpoch).HasColumnName("TaskEpoch");
                metadata.Property(item => item.SchedulePlanId).HasColumnName("SchedulePlanId");
                metadata.Property(item => item.ScheduleExpectedVersion).HasColumnName("ScheduleExpectedVersion");
                metadata.Property(item => item.ScheduleDecisionId).HasColumnName("ScheduleDecisionId");
                metadata.HasIndex(item => new { item.SubjectType, item.SubjectId }).HasDatabaseName("ix_operation_observations_subject");
                metadata.HasIndex(item => new { item.TaskId, item.TaskEpoch }).HasDatabaseName("ix_operation_observations_task");
                metadata.HasIndex(item => new { item.RootOperationId, item.RootSource }).HasDatabaseName("ix_operation_observations_root");
            });
            data.HasIndex(item => new { item.Source, item.OperationId, item.Phase }).IsUnique().HasDatabaseName("ux_operation_observations_phase");
            data.HasIndex(item => new { item.OccurredAt, item.Source, item.OperationId }).HasDatabaseName("ix_operation_observations_time");
        });
        observation.Navigation(item => item.Data).IsRequired();
    }
}

internal sealed class AuditingDbContextFactory : IDesignTimeDbContextFactory<AuditingDbContext>
{
    public AuditingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AuditingDbContext>()
        .UseNexusStackPostgres("Host=design-time;Database=design-time", AuditingDbContext.SchemaName).Options);
}
