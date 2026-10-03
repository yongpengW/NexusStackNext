using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Costing.Application;

/// <summary>Costing 在应用接口公开的稳定拒绝原因。</summary>
public static class CostingErrors
{
    /// <summary>成本变更的整批提交事实无法在所属上下文的容量内原子准入。</summary>
    public static readonly Error AuditCapacityExceeded = new("costing.audit_capacity_exhausted", "成本事实容量不足，请恢复交付或清理已确认的过期事实后重试。");
}
