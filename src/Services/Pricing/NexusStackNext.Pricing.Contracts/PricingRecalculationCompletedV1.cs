using NexusStackNext.BuildingBlocks.Application.Events;

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
}
