using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Pricing.Contracts;

/// <summary>定价对象已提交的最小变化，不携带金额、费率或计算输入。</summary>
public sealed record PriceQuoteCommittedV1 : IntegrationEvent
{
    /// <summary>固定版本化事件名。</summary>
    public const string Name = "pricing.price-quote-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>所属定价对象。</summary>
    public required Guid ItemId { get; init; }
    /// <summary>created / inputs-changed / costing-applied / result-applied。</summary>
    public required string Operation { get; init; }
    /// <summary>已提交的聚合版本。</summary>
    public required long Version { get; init; }
    /// <summary>接纳成本版本时的来源成本对象；其余动作为空。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CostingItemId { get; init; }
    /// <summary>当前认证用户；系统执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>安全追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全调查关联。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>本次执行及原始发起关系，不提供授权。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
}
