namespace NexusStackNext.Auditing.Contracts;

/// <summary>端点的静态操作说明；业务只声明元数据，不自行发布日志。</summary>
public sealed record OperationDescription
{
    /// <summary>创建固定动作及描述，不允许动态参数模板。</summary>
    /// <param name="action">稳定动作名称，最多 200 字符。</param>
    /// <param name="description">可选静态描述，最多 256 字符。</param>
    /// <param name="subject">显式路由客体；不从 body、query 或响应中读取。</param>
    public OperationDescription(string action, string? description = null, OperationSubjectRoute? subject = null)
    {
        CheckText(action, 200, nameof(action));
        if (description is not null) { CheckText(description, 256, nameof(description)); }
        Action = action;
        Description = description;
        Subject = subject;
    }

    /// <summary>稳定动作。</summary>
    public string Action { get; }
    /// <summary>静态描述。</summary>
    public string? Description { get; }
    /// <summary>明确选取的安全路由客体。</summary>
    public OperationSubjectRoute? Subject { get; }
    /// <summary>宿主在反向代理端点声明；不把边缘转发当作业务提交。</summary>
    public bool IsProxy { get; init; }

    internal static void CheckText(string value, int maximum, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximum || value.Any(char.IsControl)) { throw new ArgumentException("操作元数据必须是有界的静态文本。", parameterName); }
    }
}

/// <summary>可安全规范化的路由标识种类。</summary>
public enum OperationSubjectIdKind
{
    /// <summary>非空 Guid，输出小写 D 格式。</summary>
    Uuid,
    /// <summary>正 Int64，输出十进制字符串。</summary>
    Numeric,
}

/// <summary>只选取一个明确命名的路由参数；解析失败则不记录客体。</summary>
public sealed record OperationSubjectRoute
{
    /// <summary>声明客体类型、路由参数与标识种类。</summary>
    /// <param name="type">静态客体类型。</param>
    /// <param name="routeParameter">路由模板中的参数名。</param>
    /// <param name="kind">唯一允许的标识格式。</param>
    public OperationSubjectRoute(string type, string routeParameter, OperationSubjectIdKind kind)
    {
        OperationDescription.CheckText(type, 100, nameof(type));
        OperationDescription.CheckText(routeParameter, 100, nameof(routeParameter));
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        Type = type;
        RouteParameter = routeParameter;
        Kind = kind;
    }

    /// <summary>静态客体类型。</summary>
    public string Type { get; }
    /// <summary>指定的路由参数名。</summary>
    public string RouteParameter { get; }
    /// <summary>规范化格式。</summary>
    public OperationSubjectIdKind Kind { get; }
}

/// <summary>显式排除低价值端点；必须说明原因，优先于操作描述。</summary>
public sealed record OperationLogSuppression
{
    /// <summary>声明排除原因。</summary>
    /// <param name="reason">最多 200 字符的静态理由。</param>
    public OperationLogSuppression(string reason)
    {
        OperationDescription.CheckText(reason, 200, nameof(reason));
        Reason = reason;
    }
    /// <summary>排除原因，供端点清单检查。</summary>
    public string Reason { get; }
}
