using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Auditing;

/// <summary>已经持久化的业务行审计，不包含业务载荷或完整操作历史。</summary>
/// <param name="CreatedAt">创建时刻（UTC）。</param>
/// <param name="CreatedBy">创建操作者；系统或匿名为空。</param>
/// <param name="UpdatedAt">最后实际变更时刻；未修改为空。</param>
/// <param name="UpdatedBy">最后修改操作者；系统或匿名为空。</param>
public sealed record EntityAuditMetadata(DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? UpdatedAt, string? UpdatedBy)
{
    /// <summary>取得持久化元数据；领域新对象及未持久化的 Memory 演示对象返回空。</summary>
    /// <param name="entity">需要审计的实体。</param>
    /// <returns>已持久化元数据或空。</returns>
    public static EntityAuditMetadata? From(IAuditedEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.CreatedAt == default ? null : new(entity.CreatedAt, entity.CreatedBy, entity.UpdatedAt, entity.UpdatedBy);
    }
}
