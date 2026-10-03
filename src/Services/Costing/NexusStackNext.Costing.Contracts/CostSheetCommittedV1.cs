using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Costing.Contracts;

/// <summary>成本核算对象已提交的最小变化，不携带成本组成或计算金额。</summary>
public sealed record CostSheetCommittedV1 : IntegrationEvent
{
    /// <summary>固定版本化事件名。</summary>
    public const string Name = "costing.cost-sheet-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>所属成本核算对象。</summary>
    public required Guid ItemId { get; init; }
    /// <summary>created / inputs-changed / result-applied。</summary>
    public required string Operation { get; init; }
    /// <summary>已提交的聚合版本。</summary>
    public required long Version { get; init; }
    /// <summary>当前认证用户；后台执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>安全追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全调查关联。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>本次执行及原始发起关系，不提供授权。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
}
