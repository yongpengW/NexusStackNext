using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Scheduling.Contracts;

/// <summary>已登记的一次计划触发；EventId 即发生标识，目标上下文决定是否接受。</summary>
public sealed record ScheduleTriggeredV1 : IntegrationEvent
{
    /// <summary>版本化线路名称。</summary>
    public const string Name = "scheduling.schedule-triggered.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>所属计划。</summary>
    public required long PlanId { get; init; }
    /// <summary>计划内单调发生序号。</summary>
    public required long TriggerSequence { get; init; }
    /// <summary>原计划时刻，OccurredAt 为实际登记时刻。</summary>
    public required DateTimeOffset ScheduledAt { get; init; }
    /// <summary>受支持的业务操作。</summary>
    public required string TargetKind { get; init; }
    /// <summary>目标上下文的对象标识。</summary>
    public required Guid TargetId { get; init; }
    /// <summary>创建后台委托时已经验证的操作者。</summary>
    public required string CreatedBy { get; init; }
    /// <summary>触发与原始定义操作的关联；旧消息可以没有该字段。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? ExecutionOrigin { get; init; }
}
