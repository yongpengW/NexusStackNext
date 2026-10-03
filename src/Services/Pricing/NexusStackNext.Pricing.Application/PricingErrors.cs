using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Pricing.Application;

/// <summary>定价命令可稳定识别的业务拒绝。</summary>
public static class PricingErrors
{
    /// <summary>关键提交事实没有容量，报价与关联工作都未提交。</summary>
    public static readonly Error AuditCapacityExceeded = new("pricing.audit_capacity_exhausted", "定价审计容量不足，请稍后重试。");
}
