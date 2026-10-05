using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Platform.Application;

/// <summary>Platform 自己的恢复 HTTP 语义。</summary>
public static class SettingAuditRecoveryErrors
{
    /// <summary>原消息的停止证据或恢复代次已改变。</summary>
    public static readonly Error Conflict = new("platform.delivery_conflict", "投递状态已经改变，请重新读取。");
    /// <summary>所属可管理消息不存在。</summary>
    public static readonly Error DeliveryNotFound = new("platform.delivery_not_found", "未找到可管理的事实投递。");
    /// <summary>消息存在，但不属于模块声明的可管理事实。</summary>
    public static readonly Error Unmanaged = new("platform.delivery_recovery.unmanaged", "该消息不属于可管理的事实投递。");
    /// <summary>没有保留该请求的恢复凭据。</summary>
    public static readonly Error NotFound = new("platform.delivery_recovery.not_found", "恢复凭据不存在。");
    /// <summary>恢复输入或可信执行信息无效。</summary>
    public static readonly Error Invalid = new("platform.delivery_recovery.invalid", "恢复请求无效。");
    /// <summary>相同请求身份不允许更换条件、理由或操作者。</summary>
    public static readonly Error RequestConflict = new("platform.delivery_recovery.request_conflict", "恢复请求身份已用于不同裁决。");
    /// <summary>有限恢复凭据池已满；整个恢复不发布。</summary>
    public static readonly Error Exhausted = new("platform.delivery_recovery.exhausted", "恢复凭据容量已满。");
}
