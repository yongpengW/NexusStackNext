using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>由来源模块代码声明的恢复归属，不接受请求正文指定 schema 或事件名。</summary>
public sealed class FactDeliveryRecoverySource
{
    /// <summary>固定声明所属存储、两类可管理事件及模块自己的拒绝语义。</summary>
    /// <param name="context">小写 ASCII 所属 schema。</param>
    /// <param name="businessEventName">所属业务事实契约名。</param>
    /// <param name="policyEventName">所属容量策略事实契约名。</param>
    /// <param name="errors">模块自己的错误语义。</param>
    public FactDeliveryRecoverySource(string context, string businessEventName, string policyEventName,
        FactDeliveryRecoveryErrors errors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessEventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyEventName);
        ArgumentNullException.ThrowIfNull(errors);
        if (context.Length is < 1 or > 63 || context[0] is < 'a' or > 'z'
            || context.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        { throw new ArgumentException("恢复来源必须是模块声明的安全 schema 标识。", nameof(context)); }
        if (businessEventName == policyEventName || !businessEventName.StartsWith(context + '.', StringComparison.Ordinal)
            || !policyEventName.StartsWith(context + '.', StringComparison.Ordinal))
        { throw new ArgumentException("恢复事件必须属于声明来源且两类名称不同。", nameof(businessEventName)); }
        Context = context;
        Schema = '"' + context + '"';
        BusinessEventName = businessEventName;
        PolicyEventName = policyEventName;
        Errors = errors;
    }

    /// <summary>固定所属上下文。</summary>
    public string Context { get; }
    /// <summary>校验并引用后的所属 schema。</summary>
    public string Schema { get; }
    /// <summary>来源模块自己的拒绝语义。</summary>
    public FactDeliveryRecoveryErrors Errors { get; }
    private string BusinessEventName { get; }
    private string PolicyEventName { get; }
    internal string[] EventNames => [BusinessEventName, PolicyEventName];
    internal bool Manages(string eventName) => eventName == BusinessEventName || eventName == PolicyEventName;
}

/// <summary>由所属模块声明的错误组，共同存储不改变其 HTTP 语义。</summary>
/// <param name="Invalid">无效请求。</param>
/// <param name="Conflict">当前停止条件冲突。</param>
/// <param name="RequestConflict">已保留请求的内容或操作者冲突。</param>
/// <param name="NotFound">原凭据不存在。</param>
/// <param name="Exhausted">独立恢复池满额。</param>
/// <param name="Unmanaged">不在来源事件白名单中。</param>
/// <param name="DeliveryNotFound">所属可管理消息不存在。</param>
public sealed record FactDeliveryRecoveryErrors(Error Invalid, Error Conflict, Error RequestConflict,
    Error NotFound, Error Exhausted, Error Unmanaged, Error DeliveryNotFound);
