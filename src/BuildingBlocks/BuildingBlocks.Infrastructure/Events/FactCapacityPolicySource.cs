using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>由模块代码固定声明所属 schema 与事实契约；从不接受 HTTP 指定来源。</summary>
public sealed class FactCapacityPolicySource
{
    private readonly Func<FactCapacityPolicyChange, FactCapacityPolicyChanged> _createFact;

    /// <summary>声明一个来源及其固定 Contracts 投影。</summary>
    /// <param name="context">所属 schema，只接受有界的小写 ASCII 标识。</param>
    /// <param name="createFact">来源 Contracts 的固定事件投影。</param>
    public FactCapacityPolicySource(string context, Func<FactCapacityPolicyChange, FactCapacityPolicyChanged> createFact)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(createFact);
        if (context.Length is < 1 or > 63 || context[0] is < 'a' or > 'z'
            || context.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new ArgumentException("策略来源必须是所属模块声明的安全 schema 标识。", nameof(context));
        }
        Context = context;
        Schema = '"' + context + '"';
        EventName = context + ".fact-capacity-policy-changed.v1";
        Invalid = new(context + ".audit_policy.invalid", "容量策略调整请求不合法。");
        Conflict = new(context + ".audit_policy.conflict", "容量策略已变化或请求标识已用于其他裁决。");
        Exhausted = new(context + ".audit_policy.control_exhausted", "容量策略控制记录额度不足，本次调整未提交。");
        _createFact = createFact;
    }

    /// <summary>固定所属上下文。</summary>
    public string Context { get; }
    /// <summary>经过校验与引用的固定 schema。</summary>
    public string Schema { get; }
    /// <summary>固定所属事件名。</summary>
    public string EventName { get; }
    /// <summary>所属请求错误。</summary>
    public Error Invalid { get; }
    /// <summary>所属条件冲突。</summary>
    public Error Conflict { get; }
    /// <summary>所属有限控制池拒绝。</summary>
    public Error Exhausted { get; }

    /// <summary>生成所属固定事件；错误装配必须拒绝，不能伪称来源。</summary>
    /// <param name="change">已经准备好的安全数值变化。</param>
    /// <returns>来源 Contracts 声明的固定事件。</returns>
    public FactCapacityPolicyChanged CreateFact(FactCapacityPolicyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var fact = _createFact(change);
        if (fact is null || fact.EventName != EventName)
        {
            throw new InvalidOperationException("容量策略事实投影与所属来源不一致。");
        }
        return fact;
    }
}
