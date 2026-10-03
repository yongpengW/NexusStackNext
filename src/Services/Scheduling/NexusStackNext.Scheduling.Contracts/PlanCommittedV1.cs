using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Scheduling.Contracts;

/// <summary>计划已经提交的状态变化；不携带编码、规则或目标业务载荷。</summary>
public sealed record PlanCommittedV1 : IntegrationEvent
{
    /// <summary>稳定事件名。</summary>
    public const string Name = "scheduling.plan-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>Scheduling 拥有的计划标识。</summary>
    public required long PlanId { get; init; }
    /// <summary>固定的状态变化名称。</summary>
    public required string Operation { get; init; }
    /// <summary>计划的已提交聚合版本。</summary>
    public required long Version { get; init; }
    /// <summary>本次已登记的调度决定；管理变化不携带该标识。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DecisionId { get; init; }
    /// <summary>当前认证的操作者；系统执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全调查关联。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>当前执行和原发起关系，不提供业务授权。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
}
