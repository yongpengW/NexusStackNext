using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.Auditing.Domain.Entries;
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

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InboxMessage>().Property<string?>(PayloadHashProperty).HasMaxLength(64);
        var entry = modelBuilder.Entity<AuditEntry>();
        entry.ToTable("audit_entries");
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
            fact.HasIndex(item => new { item.EventName, item.MessageId }).IsUnique();
        });
        entry.Navigation(item => item.Fact).IsRequired();
    }
}

internal sealed class AuditingDbContextFactory : IDesignTimeDbContextFactory<AuditingDbContext>
{
    public AuditingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AuditingDbContext>()
        .UseNexusStackPostgres("Host=design-time;Database=design-time", AuditingDbContext.SchemaName).Options);
}
