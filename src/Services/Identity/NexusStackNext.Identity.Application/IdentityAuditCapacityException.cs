using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>存储适配器无法接纳本次审计事实；命令事务必须整体回滚。</summary>
public sealed class IdentityAuditCapacityException : Exception
{
    /// <summary>稳定拒绝原因，不含存储实现或消息内容。</summary>
    public static readonly Error Exhausted = new("identity.audit_capacity.exhausted", "审计事实存储容量不足，本次变更未提交，请稍后重试。");

    /// <summary>创建容量拒绝。</summary>
    public IdentityAuditCapacityException() : base(Exhausted.Message) { }
}
