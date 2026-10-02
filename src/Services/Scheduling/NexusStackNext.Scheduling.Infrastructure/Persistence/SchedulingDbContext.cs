using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NexusStackNext.BuildingBlocks.Application.Operations;
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
    internal const string ExecutionOriginProperty = "ExecutionOrigin";
    /// <summary>计划定义。</summary>
    public DbSet<ScheduledTask> Plans => Set<ScheduledTask>();
    /// <summary>本上下文已登记的触发事实。</summary>
    public DbSet<ScheduleOccurrence> Occurrences => Set<ScheduleOccurrence>();
    /// <summary>不可变的调度决定，含跳过和合并事实。</summary>
    public DbSet<ScheduleDecision> Decisions => Set<ScheduleDecision>();

    internal async Task VerifyStorageAsync(CancellationToken cancellationToken)
    {
        // 迁移历史存在不保证表/列仍完整；每个读面最多取一行，同时验证所需列。
        _ = await Plans.AsNoTracking().Take(1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        _ = await Decisions.AsNoTracking().Take(1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        _ = await Occurrences.AsNoTracking().Take(1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        _ = await Outbox.AsNoTracking().Take(1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

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
        plan.Property(task => task.DelegatedBy).HasMaxLength(128).IsRequired();
        plan.Property<ExecutionOrigin?>(ExecutionOriginProperty).HasColumnType("jsonb").HasConversion(
            origin => JsonSerializer.Serialize(origin, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<ExecutionOrigin>(json, JsonSerializerOptions.Default))
            .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        plan.Property(task => task.Version).IsConcurrencyToken().ValueGeneratedNever();
        plan.Property(task => task.LastSchedulingErrorCode).HasMaxLength(96);
        plan.Ignore(task => task.Interval);
        plan.OwnsOne(task => task.Rule, rule =>
        {
            rule.Property(value => value.Kind).HasColumnName("RuleKind").HasMaxLength(16).IsRequired();
            rule.Property(value => value.Expression).HasColumnName("Expression").HasMaxLength(256);
            rule.Property(value => value.TimeZoneId).HasColumnName("TimeZoneId").HasMaxLength(128);
            rule.Property(value => value.CronFieldCount).HasColumnName("CronFieldCount");
            rule.Property(value => value.Day).HasColumnName("Day");
            rule.Property(value => value.Hour).HasColumnName("Hour");
            rule.Property(value => value.Minute).HasColumnName("Minute");
            rule.Property(value => value.MisfirePolicy).HasColumnName("MisfirePolicy").HasMaxLength(16);
            rule.Property(value => value.GraceSeconds).HasColumnName("GraceSeconds");
            // 保留旧 Interval 列与数据库 interval 类型，既有行不需要损失精度的重写。
            rule.Property(value => value.IntervalSeconds).HasColumnName("Interval")
                .HasConversion(new ValueConverter<double, TimeSpan>(seconds => ScheduleRule.NormalizeInterval(seconds), interval => interval.TotalSeconds));
        });
        plan.Navigation(task => task.Rule).IsRequired();
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
        occurrence.Property(item => item.ExecutionOrigin).HasColumnType("jsonb").HasConversion(
            origin => JsonSerializer.Serialize(origin, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<ExecutionOrigin>(json, JsonSerializerOptions.Default));
        var decision = modelBuilder.Entity<ScheduleDecision>();
        decision.ToTable("decisions");
        decision.HasKey(item => item.DecisionId);
        decision.HasIndex(item => new { item.PlanId, item.PlanVersion }).IsUnique().HasDatabaseName("ux_decisions_plan_version");
        decision.Property(item => item.Kind).HasMaxLength(16).IsRequired();
        decision.Property(item => item.Rule).HasColumnType("jsonb").HasConversion(
            rule => JsonSerializer.Serialize(rule, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<ScheduleRule>(json, JsonSerializerOptions.Default)!);
    }
}

internal sealed class SchedulingDbContextFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SchedulingDbContext>()
        .UseNexusStackPostgres("Host=design-time;Database=design-time", SchedulingDbContext.SchemaName).Options);
}
