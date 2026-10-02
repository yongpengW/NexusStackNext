using System.Text.Json.Serialization;

namespace NexusStackNext.Auditing.Contracts;

/// <summary>固定字段的安全执行元数据；不接受属性包或完整业务载荷。</summary>
public sealed record OperationDetails
{
    /// <summary>代码声明的稳定动作名称。</summary>
    public required string Action { get; init; }
    /// <summary>endpoint 表示本地处理，proxy 表示边缘转发；均不是提交事实。</summary>
    public required string ExecutionRole { get; init; }
    /// <summary>代码声明的静态描述，不进行参数插值。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
    /// <summary>明确声明的客体类型，与标识种类和值同时提供。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectType { get; init; }
    /// <summary>客体标识种类：guid 或 int64。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectIdKind { get; init; }
    /// <summary>规范化的标识字符串；不包含原始路由值或大整数 JSON 数值。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectId { get; init; }
    /// <summary>本次执行的 W3C span；仅用于关联。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpanId { get; init; }
    /// <summary>已观察到的父 span；不推测跨代理的直接父子关系。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSpanId { get; init; }
    /// <summary>有界的关联值；不能用于认证、授权或去重。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }
}
