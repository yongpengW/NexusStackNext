using System.Text.Json.Serialization;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Auditing.Contracts;

/// <summary>执行开始或完成的安全观察；不证明业务事务已经提交。</summary>
public sealed record OperationObservedV1 : IntegrationEvent
{
    /// <summary>稳定事件名。</summary>
    public const string Name = "auditing.operation-observed.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>来源生成的一次执行标识；开始和完成共享此值。</summary>
    public required Guid OperationId { get; init; }
    /// <summary>宿主配置的可信来源名称。</summary>
    public required string Source { get; init; }
    /// <summary>执行入口种类；当前契约接纳 http。</summary>
    public required string Kind { get; init; }
    /// <summary>观察阶段：started 或 finished。</summary>
    public required string Phase { get; init; }
    /// <summary>完成观察：completed、accepted、rejected、failed 或 canceled；开始时为空。</summary>
    public string? Outcome { get; init; }
    /// <summary>认证后的当前执行者；未认证或系统执行为空。</summary>
    public string? ActorId { get; init; }
    /// <summary>来源生成的追踪标识，不作为身份或去重凭据。</summary>
    public required string TraceId { get; init; }
    /// <summary>HTTP 方法；不包含请求头或参数。</summary>
    public string? HttpMethod { get; init; }
    /// <summary>端点路由模板；未匹配端点为空，不允许用原始 URL 代替。</summary>
    public string? RouteTemplate { get; init; }
    /// <summary>已观察到的 HTTP 状态码；开始及未产生响应时为空。</summary>
    public int? StatusCode { get; init; }
    /// <summary>来源单调计时器测得的耗时毫秒数；开始时为空。</summary>
    public long? DurationMs { get; init; }
    /// <summary>显式的安全执行描述；缺失时保留旧消息形状，不补造描述或客体。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationDetails? Metadata { get; init; }
}
