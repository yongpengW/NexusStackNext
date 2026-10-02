using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>普通操作观察共用的写入与降级政策；业务结果由各入口分类。</summary>
internal sealed partial class OperationObservationWriter(IOperationJournal journal, OperationJournalStatus status,
    OperationCaptureOptions options, ILogger<OperationObservationWriter> logger)
{
    public async Task WriteAsync(OperationObservedV1 observation)
    {
        // 请求或任务取消不取消日志落盘；每条写入自带独立且有界的超时。
        using var timeout = new CancellationTokenSource(options.WriteTimeout);
        try
        {
            if ((await journal.AppendAsync(observation, timeout.Token).ConfigureAwait(false)).IsSuccess) { return; }
        }
        catch (Exception) { /* 普通观察降级不能改变业务结果；原异常可能包含秘密，不输出。 */ }
        ReportFailure(observation.OperationId, observation.Phase);
    }

    public void ReportFailure(Guid operationId, string phase)
    {
        status.ReportFailure();
        try { LogCaptureFailed(operationId, phase); }
        catch (Exception) { /* 诊断提供器故障同样不能改变业务结果；失败计数仍然可见。 */ }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "操作观察未持久化：OperationId={OperationId}，Phase={Phase}；操作日志已降级。")]
    private partial void LogCaptureFailed(Guid operationId, string phase);
}
