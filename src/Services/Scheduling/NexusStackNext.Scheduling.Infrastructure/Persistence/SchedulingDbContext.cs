using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure.Persistence;

/// <summary>Scheduling 拥有的计划、发生与消息存储。</summary>
/// <param name="options">数据库配置。</param>
public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : NexusStackDbContext(options, SchemaName)
{
    /// <summary>所属 schema。</summary>
    public const string SchemaName = "scheduling";
    /// <summary>计划定义。</summary>
    public DbSet<ScheduledTask> Plans => Set<ScheduledTask>();
    /// <summary>本上下文已登记的触发事实。</summary>
    public DbSet<ScheduleOccurrence> Occurrences => Set<ScheduleOccurrence>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var plan = modelBuilder.Entity<ScheduledTask>();
        plan.ToTable("plans");
        plan.HasKey(task => task.Id);
        plan.Property(task => task.Id).HasConversion(id => id.Value, value => new ScheduledTaskId(value));
        plan.Property(task => task.Code).HasConversion(code => code.Value, value => TaskCode.Create(value).Value)
            .HasMaxLength(TaskCode.MaxLength).IsRequired();
        plan.HasIndex(task => task.Code).IsUnique().HasDatabaseName("ux_plans_code");
        plan.Property(task => task.CreatedBy).HasMaxLength(128).IsRequired();
        plan.Property(task => task.Version).IsConcurrencyToken().ValueGeneratedNever();
        plan.HasIndex(task => new { task.IsEnabled, task.NextRunAt, task.Id }).HasDatabaseName("ix_plans_due");
        plan.OwnsOne(task => task.Target, target =>
        {
            target.Property(value => value.Kind).HasColumnName("TargetKind").HasMaxLength(TaskCode.MaxLength).IsRequired();
            target.Property(value => value.SubjectId).HasColumnName("TargetId");
        });
        plan.Navigation(task => task.Target).IsRequired();
        var occurrence = modelBuilder.Entity<ScheduleOccurrence>();
        occurrence.ToTable("occurrences");
        occurrence.HasKey(item => item.OccurrenceId);
        occurrence.HasIndex(item => new { item.PlanId, item.TriggerSequence }).IsUnique().HasDatabaseName("ux_occurrences_plan_sequence");
        occurrence.Property(item => item.TargetKind).HasMaxLength(TaskCode.MaxLength).IsRequired();
        occurrence.Property(item => item.CreatedBy).HasMaxLength(128).IsRequired();
    }
}

internal sealed class SchedulingDbContextFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SchedulingDbContext>()
        .UseNexusStackPostgres("Host=design-time;Database=design-time", SchedulingDbContext.SchemaName).Options);
}
