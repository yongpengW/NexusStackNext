using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Platform.Application;

/// <summary>策略管理的安全稳定错误。</summary>
public static class SettingFactCapacityPolicyErrors
{
    /// <summary>请求或可信执行信息不合法。</summary>
    public static readonly Error Invalid = new("platform.audit_policy.invalid", "容量策略调整请求不合法。");
    /// <summary>旧版本、同请求异内容或异操作者。</summary>
    public static readonly Error Conflict = new("platform.audit_policy.conflict", "容量策略已变化或请求标识已用于其他裁决。");
    /// <summary>独立控制额度不足，策略未改变。</summary>
    public static readonly Error Exhausted = new("platform.audit_policy.control_exhausted", "容量策略控制记录额度不足，本次调整未提交。");
}
