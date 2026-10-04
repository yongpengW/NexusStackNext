using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Identity.Application;

/// <summary>存储适配器无法接纳本次审计事实；命令事务必须整体回滚。</summary>
public sealed class IdentityAuditCapacityException : Exception
{
    /// <summary>稳定拒绝原因，不含存储实现或消息内容。</summary>
    public static readonly Error Exhausted = new("identity.audit_capacity.exhausted", "审计事实存储容量不足，本次变更未提交，请稍后重试。");

    /// <summary>创建容量拒绝。</summary>
    public IdentityAuditCapacityException() : this(Exhausted) { }

    /// <summary>创建存储适配器翻译出的安全容量拒绝。</summary>
    /// <param name="reason">不含数据库诊断的稳定拒绝原因。</param>
    public IdentityAuditCapacityException(Error reason) : base((reason ?? throw new ArgumentNullException(nameof(reason))).Message) => Reason = reason;

    /// <summary>本次拒绝的安全原因，区分额度耗尽与锁争用。</summary>
    public Error Reason { get; }
}
