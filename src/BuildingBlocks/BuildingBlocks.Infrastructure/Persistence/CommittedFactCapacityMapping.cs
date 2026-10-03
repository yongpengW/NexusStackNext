using Microsoft.EntityFrameworkCore;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>所属上下文的事实容量模型；额度和占用随数据库持久化，不由宿主配置覆盖。</summary>
public static class CommittedFactCapacityMapping
{
    /// <summary>在当前模型的独占 schema 中映射容量账本。</summary>
    /// <param name="modelBuilder">所属模型。</param>
    /// <returns>原模型。</returns>
    public static ModelBuilder ConfigureCommittedFactCapacity(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var capacity = modelBuilder.Entity<CommittedFactCapacity>();
        capacity.ToTable("fact_capacity", table =>
        {
            table.HasCheckConstraint("ck_fact_capacity_singleton", "\"Id\" = 1");
            table.HasCheckConstraint("ck_fact_capacity_bounds", "\"RetainedRecords\" >= 0 AND \"RetainedPayloadBytes\" >= 0 AND \"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\"");
        });
        capacity.HasKey(item => item.Id);
        return modelBuilder;
    }
}

internal sealed class CommittedFactCapacity
{
    public int Id { get; set; }
    public long RetainedRecords { get; set; }
    public long RetainedPayloadBytes { get; set; }
    public long MaxRecords { get; set; }
    public long MaxPayloadBytes { get; set; }
    public int MaxRecordPayloadBytes { get; set; }
}
