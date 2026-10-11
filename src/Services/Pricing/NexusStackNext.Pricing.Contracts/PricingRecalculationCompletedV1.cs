using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Pricing.Contracts;

/// <summary>Pricing 重算结果已提交的最小外部通知契约。</summary>
public sealed record PricingRecalculationCompletedV1 : IntegrationEvent
{
    /// <summary>固定版本化事件名。</summary>
    public const string Name = "pricing.recalculation-completed.v1";

    /// <inheritdoc />
    public override string EventName => Name;

    /// <summary>定价对象。</summary>
    public required Guid ItemId { get; init; }

    /// <summary>提交后的聚合版本。</summary>
    public required long Version { get; init; }

    /// <summary>本次重算使用的输入版本。</summary>
    public required long InputRevision { get; init; }

    /// <summary>提交后的结果版本。</summary>
    public required long CalculatedRevision { get; init; }

    /// <summary>稳定的外部幂等键；同一事件重投必须复用它。</summary>
    public required Guid IdempotencyKey { get; init; }

    /// <summary>安全追踪标识。</summary>
    public required string TraceId { get; init; }

    /// <summary>安全关联标识。</summary>
    public required string CorrelationId { get; init; }

    /// <summary>触发本次重算的可信操作来源；只用于调查关联。</summary>
    public ExecutionOrigin? Execution { get; init; }
}

/// <summary>受控接收端确认 Pricing 重算通知的响应契约。</summary>
public sealed record PricingWebhookAcceptedV1
{
    /// <summary>确认的事件标识。</summary>
    public required Guid EventId { get; init; }

    /// <summary>确认的幂等键。</summary>
    public required Guid IdempotencyKey { get; init; }

    /// <summary>接收端已接受并持久化该通知；仅 GET 核对的 200 响应可用 false 明确证明尚未接受。</summary>
    public required bool Accepted { get; init; }
}
