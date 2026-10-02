namespace NexusStackNext.Auditing.Domain.Operations;

/// <summary>单条不可变观察的消息身份；开始与完成拥有不同身份。</summary>
/// <param name="Value">来源提供的消息标识。</param>
public readonly record struct OperationObservationId(Guid Value);

/// <summary>一次执行的身份；开始与完成共享此标识，并与来源共同定位执行。</summary>
/// <param name="Value">来源提供的执行标识。</param>
public readonly record struct OperationId(Guid Value);
