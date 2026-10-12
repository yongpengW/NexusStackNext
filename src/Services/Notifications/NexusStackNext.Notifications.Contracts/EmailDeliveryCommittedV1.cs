using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Notifications.Contracts;

/// <summary>邮件生命周期的最小提交事实，不包含地址、主题、正文、附件或 SMTP 配置。</summary>
public sealed record EmailDeliveryCommittedV1 : IntegrationEvent
{
    /// <summary>版本化事件名。</summary>
    public const string Name = "notifications.email-delivery-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>邮件意图身份。</summary>
    public required Guid EmailId { get; init; }
    /// <summary>submitted、attempt-started、accepted、failed、retry、resend 或 confirm。</summary>
    public required string Operation { get; init; }
    /// <summary>邮件聚合的已提交版本。</summary>
    public required long Version { get; init; }
    /// <summary>当前尝试次数。</summary>
    public required int Attempt { get; init; }
    /// <summary>当前操作者；系统执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>安全追踪关联。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全调用关联。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>原发起关系，不授予权限。</summary>
    public ExecutionOrigin? Execution { get; init; }
}
