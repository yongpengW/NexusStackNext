using NexusStackNext.BuildingBlocks.Infrastructure.Exports;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>事实与操作观察分别使用固定列集；所有单元格均为精确文本。</summary>
public static class AuditXlsxV1
{
    private static readonly string[] Facts = ["EvidenceKind", "EntryId", "MessageId", "EventName", "Source", "Action", "SubjectType", "SubjectId", "SubjectVersion",
        "ActorId", "OccurredAt", "RecordedAt", "TraceId", "CorrelationId", "OperationId", "OperationSource", "RootOperationId", "RootSource", "InitiatorId",
        "RelatedContext", "RelatedSubjectType", "RelatedSubjectId"];
    private static readonly string[] Operations = ["EvidenceKind", "OperationId", "Source", "Kind", "Action", "ExecutionRole", "SubjectType", "SubjectId", "ActorId",
        "TraceId", "CorrelationId", "StartedAt", "FinishedAt", "Outcome", "HttpMethod", "RouteTemplate", "StatusCode", "DurationMs", "RootOperationId", "RootSource",
        "ParentOperationId", "ParentSource", "InitiatorId", "TaskId", "TaskEpoch", "SchedulePlanId", "ScheduleExpectedVersion", "ScheduleDecisionId"];

    /// <summary>生成完整工作簿；空单元格保留缺失证据，公式状文本不会成为公式。</summary>
    /// <param name="kind">facts 或 operations。</param>
    /// <param name="rows">已冻结白名单行。</param>
    /// <param name="output">调用方拥有的可读写空流。</param>
    /// <param name="maxBytes">最多 32 MiB 的实际输出。</param>
    /// <param name="cancellationToken">执行预算。</param>
    /// <returns>完整已校验工作簿。</returns>
    public static Task WriteAsync(string kind, IReadOnlyList<string[]> rows, Stream output, long maxBytes = 33_554_432, CancellationToken cancellationToken = default) => kind switch
    {
        "facts" => TextWorkbook.WriteAsync("CommittedFacts", Facts, rows, output, maxBytes, cancellationToken),
        "operations" => TextWorkbook.WriteAsync("OperationObservations", Operations, rows, output, maxBytes, cancellationToken),
        _ => throw new ArgumentException("调查证据类别无效。", nameof(kind)),
    };
}
