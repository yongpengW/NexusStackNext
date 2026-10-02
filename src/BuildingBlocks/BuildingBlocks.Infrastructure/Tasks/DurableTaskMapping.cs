using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tasks;

/// <summary>两个任务上下文共同使用的执行记录映射；业务字段与聚合关系由上下文添加。</summary>
public static class DurableTaskMapping
{
    /// <summary>配置本上下文 schema 内的 tasks / attempts 执行协议。</summary>
    /// <typeparam name="TTask">本上下文的输入快照类型。</typeparam>
    /// <param name="modelBuilder">所属上下文的模型。</param>
    public static void Configure<TTask>(ModelBuilder modelBuilder) where TTask : DurableTaskRecord
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var task = modelBuilder.Entity<TTask>();
        task.ToTable("tasks");
        task.HasKey(x => x.TaskId);
        task.Property(x => x.TaskId).ValueGeneratedNever();
        task.Property(x => x.ExecutionOrigin).HasColumnType("jsonb").HasConversion(
            origin => JsonSerializer.Serialize(origin, (JsonSerializerOptions?)null),
            json => JsonSerializer.Deserialize<ExecutionOrigin>(json, (JsonSerializerOptions?)null));
        task.Property(x => x.State).HasMaxLength(24);
        task.Property(x => x.ErrorCode).HasMaxLength(64);
        task.Property(x => x.AvailableAt).HasDefaultValueSql("clock_timestamp()");
        task.HasIndex(x => new { x.State, x.AvailableAt });
        var attempt = modelBuilder.Entity<DurableTaskAttempt>();
        attempt.ToTable("attempts");
        attempt.HasKey(x => new { x.TaskId, x.Epoch });
        attempt.Property(x => x.Outcome).HasMaxLength(24);
        attempt.Property(x => x.ErrorCode).HasMaxLength(64);
        attempt.HasOne<TTask>().WithMany(x => x.History).HasForeignKey(x => x.TaskId);
    }
}
