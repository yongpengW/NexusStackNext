using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Platform.Contracts;

/// <summary>设置已提交的最小通知；不携带设置值、说明或完整请求。</summary>
public sealed record SettingCommittedV1 : IntegrationEvent
{
    /// <summary>稳定事件名。</summary>
    public const string Name = "platform.setting-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>稳定设置键。</summary>
    public required string Key { get; init; }
    /// <summary>创建、变更或清空：created / changed / cleared。</summary>
    public required string Operation { get; init; }
    /// <summary>提交后的聚合版本。</summary>
    public required long Version { get; init; }
    /// <summary>来源服务认证的操作者；系统动作为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源执行追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>来源执行关联标识。</summary>
    public required string CorrelationId { get; init; }
}
