namespace NexusStackNext.BuildingBlocks.Application.Operations;

/// <summary>来源模块生成的执行关联；只用于调查，不能作为授权或幂等依据。</summary>
/// <param name="OperationId">直接触发后续工作的操作。</param>
/// <param name="Source">该操作所属来源。</param>
/// <param name="RootOperationId">原始操作。</param>
/// <param name="RootSource">原始操作所属来源。</param>
/// <param name="InitiatorId">原发起人，与后台执行者分开。</param>
/// <param name="TraceId">追踪关联。</param>
/// <param name="CorrelationId">经过规范化的调用关联。</param>
public sealed record ExecutionOrigin(Guid OperationId, string Source, Guid RootOperationId, string RootSource,
    string? InitiatorId, string TraceId, string? CorrelationId = null)
{
    /// <summary>接收消息前检查有界关联字段；通过不代表调用者具备业务权限。</summary>
    /// <returns>关联字段可安全持久化。</returns>
    public bool IsValid() => OperationId != Guid.Empty && RootOperationId != Guid.Empty
        && Safe(Source, 64) && Safe(RootSource, 64) && Safe(TraceId, 128)
        && (InitiatorId is null || Safe(InitiatorId, 200))
        && (CorrelationId is null || CorrelationId.Length is > 0 and <= 64
            && CorrelationId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

    private static bool Safe(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
}

/// <summary>读取当前调用链的安全来源；不读取 HTTP 参数，不授予调用身份。</summary>
public interface IExecutionContext
{
    /// <summary>当前显式执行是否为系统操作；只用于归属记录，不授予权限。</summary>
    bool IsSystem { get; }

    /// <summary>取得可随所属业务记录持久化的快照；无当前操作时为空。</summary>
    /// <returns>来源快照。</returns>
    ExecutionOrigin? Capture();
}

internal sealed class EmptyExecutionContext : IExecutionContext
{
    public bool IsSystem => false;
    public ExecutionOrigin? Capture() => null;
}
