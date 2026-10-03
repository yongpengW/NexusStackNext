using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>由事实生产上下文显式接入清理查询的索引。</summary>
public static class CommittedFactCleanupMapping
{
    /// <summary>匹配事件、确认期限和稳定批次排序；排除未确认与死信。</summary>
    /// <param name="modelBuilder">所属模型。</param>
    /// <returns>原模型。</returns>
    public static ModelBuilder ConfigureCommittedFactCleanup(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<OutboxEntry>().HasIndex(entry => new { entry.EventName, entry.DeliveredAt, entry.Id })
            .HasDatabaseName("ix_outbox_confirmed_event")
            .HasFilter("\"DeliveredAt\" IS NOT NULL AND \"DeadLetteredAt\" IS NULL");
        return modelBuilder;
    }
}
