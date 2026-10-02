using System.Text.Json.Serialization;

namespace NexusStackNext.Auditing.Contracts;

/// <summary>固定字段的安全执行元数据；不接受属性包或完整业务载荷。</summary>
public sealed record OperationDetails
{
    /// <summary>代码声明的稳定动作名称。</summary>
    public required string Action { get; init; }
    /// <summary>endpoint / proxy / command / task / schedule 区分执行入口；均不是提交事实。</summary>
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
    /// <summary>根操作标识，由来源模块生成。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RootOperationId { get; init; }
    /// <summary>根操作所属来源。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootSource { get; init; }
    /// <summary>直接触发当前执行的操作标识。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentOperationId { get; init; }
    /// <summary>直接触发操作的来源。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSource { get; init; }
    /// <summary>原发起人；不赋予当前后台执行者身份。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiatorId { get; init; }
    /// <summary>所属来源的任务标识。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TaskId { get; init; }
    /// <summary>任务的租约代次。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TaskEpoch { get; init; }
    /// <summary>所属来源的计划标识。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SchedulePlanId { get; init; }
    /// <summary>本次调度读取的计划版本。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ScheduleExpectedVersion { get; init; }
    /// <summary>本次拟登记的调度决定；必须结合结果判断是否已登记。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ScheduleDecisionId { get; init; }
}
