using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.Identity.Contracts;

/// <summary>Identity 已提交的最小实体事实；不含身份资料、凭据或业务字段原值。</summary>
public sealed record IdentityEntityCommittedV1 : IntegrationEvent
{
    /// <summary>稳定事件名。</summary>
    public const string Name = "identity.entity-committed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>拥有变化的聚合类型。</summary>
    public required string SubjectType { get; init; }
    /// <summary>聚合内部标识，不是用户名或令牌。</summary>
    public required string SubjectId { get; init; }
    /// <summary>固定的已提交变化分类。</summary>
    public required string Operation { get; init; }
    /// <summary>提交的聚合版本。</summary>
    public required long Version { get; init; }
    /// <summary>来源认证的当前执行者；系统或匿名注册为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源追踪标识。</summary>
    public required string TraceId { get; init; }
    /// <summary>安全的调查关联。</summary>
    public required string CorrelationId { get; init; }
    /// <summary>当前操作及原发起关系；未采集时为空。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionOrigin? Execution { get; init; }
    /// <summary>明确的关联客体；只允许本上下文内部标识，不含显示名或原始输入。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySubjectReference? RelatedSubject { get; init; }
}

/// <summary>Identity 事实引用的已知客体。</summary>
/// <param name="Type">由动作决定的已知类型。</param>
/// <param name="Id">内部正整数标识。</param>
public sealed record IdentitySubjectReference(string Type, long Id);
