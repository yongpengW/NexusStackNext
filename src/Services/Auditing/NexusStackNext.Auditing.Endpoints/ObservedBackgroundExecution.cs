using System.Diagnostics;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed partial class ObservedBackgroundExecution(IOperationJournal journal, OperationJournalStatus status,
    OperationCaptureOptions options, IClock clock, OperationExecutionContext context, ILogger<ObservedBackgroundExecution> logger) : IBackgroundExecutionObservation
{
    public async Task<TResult> ObserveAsync<TInput, TResult>(BackgroundExecutionDescriptor descriptor, Func<Task<BackgroundExecutionInput<TInput>>> prepare, Func<TInput, Task<TResult>> execute,
        Func<TResult, BackgroundExecutionOutcome> classify, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(classify);
        var operationId = Guid.NewGuid();
        var startedAt = clock.UtcNow;
        var timer = Stopwatch.StartNew();
        BackgroundExecutionInput<TInput> input;
        try { input = await prepare().ConfigureAwait(false); }
        catch (Exception error)
        {
            // 输入尚未读到时不猜测来源；已在描述中提供的计划委托人仍是已知信息。
            var interrupted = Started(descriptor, operationId, startedAt, null);
            await RecordAsync(interrupted).ConfigureAwait(false);
            await RecordAsync(interrupted with
            {
                EventId = Guid.NewGuid(),
                Phase = "finished",
                OccurredAt = clock.UtcNow,
                DurationMs = timer.ElapsedMilliseconds,
                Outcome = error is OperationCanceledException && cancellationToken.IsCancellationRequested ? "canceled" : "failed",
            }).ConfigureAwait(false);
            throw;
        }
        var parent = input.Origin;
        var rootId = parent?.RootOperationId ?? operationId;
        var rootSource = parent?.RootSource ?? options.Source;
        var traceId = parent?.TraceId ?? Activity.Current?.TraceId.ToString() ?? operationId.ToString("N");
        var started = Started(descriptor, operationId, startedAt, parent);
        await RecordAsync(started).ConfigureAwait(false);
        using var operationScope = context.Enter(new ExecutionOrigin(operationId, options.Source, rootId, rootSource,
            started.Metadata!.InitiatorId, traceId, parent?.CorrelationId), system: true);
        string? outcome = null;
        try
        {
            var result = await execute(input.Value).ConfigureAwait(false);
            try
            {
                outcome = classify(result) switch
                {
                    BackgroundExecutionOutcome.Completed => "completed",
                    BackgroundExecutionOutcome.Superseded => "superseded",
                    BackgroundExecutionOutcome.LeaseLost => "lease_lost",
                    BackgroundExecutionOutcome.Accepted => "accepted",
                    BackgroundExecutionOutcome.Skipped => "skipped",
                    BackgroundExecutionOutcome.Rejected => "rejected",
                    BackgroundExecutionOutcome.Failed => "failed",
                    _ => null,
                };
            }
            catch (Exception) { /* 分类故障不应篡改已经得到的业务结果。 */ }
            if (outcome is null) { ReportFailure(operationId, "classification"); }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (Exception)
        {
            outcome = "failed";
            throw;
        }
        finally
        {
            operationScope.Dispose();
            if (outcome is not null)
            {
                await RecordAsync(started with
                {
                    EventId = Guid.NewGuid(),
                    Phase = "finished",
                    OccurredAt = clock.UtcNow,
                    DurationMs = timer.ElapsedMilliseconds,
                    Outcome = outcome,
                }).ConfigureAwait(false);
            }
        }
    }

    private OperationObservedV1 Started(BackgroundExecutionDescriptor descriptor, Guid operationId, DateTimeOffset startedAt, ExecutionOrigin? parent) => new()
    {
        EventId = Guid.NewGuid(),
        OperationId = operationId,
        Source = options.Source,
        Kind = descriptor is ScheduleExecutionDescriptor ? "schedule" : "task",
        Phase = "started",
        OccurredAt = startedAt,
        TraceId = parent?.TraceId ?? Activity.Current?.TraceId.ToString() ?? operationId.ToString("N"),
        Metadata = new OperationDetails
        {
            Action = descriptor.Action,
            ExecutionRole = descriptor is ScheduleExecutionDescriptor ? "schedule" : "task",
            RootOperationId = parent?.RootOperationId ?? operationId,
            RootSource = parent?.RootSource ?? options.Source,
            ParentOperationId = parent?.OperationId,
            ParentSource = parent?.Source,
            InitiatorId = parent?.InitiatorId ?? (descriptor as ScheduleExecutionDescriptor)?.InitiatorId,
            TaskId = (descriptor as TaskExecutionDescriptor)?.TaskId,
            TaskEpoch = (descriptor as TaskExecutionDescriptor)?.Epoch,
            SchedulePlanId = (descriptor as ScheduleExecutionDescriptor)?.PlanId,
            ScheduleExpectedVersion = (descriptor as ScheduleExecutionDescriptor)?.ExpectedVersion,
            ScheduleDecisionId = (descriptor as ScheduleExecutionDescriptor)?.DecisionId,
            CorrelationId = parent?.CorrelationId,
        },
    };

    private async Task RecordAsync(OperationObservedV1 observation)
    {
        using var timeout = new CancellationTokenSource(options.WriteTimeout);
        try
        {
            if ((await journal.AppendAsync(observation, timeout.Token).ConfigureAwait(false)).IsSuccess) { return; }
        }
        catch (Exception) { /* 来源采集故障不改变任务结果；不输出载荷或异常正文。 */ }
        ReportFailure(observation.OperationId, observation.Phase);
    }

    private void ReportFailure(Guid operationId, string phase)
    {
        status.ReportFailure();
        try { LogCaptureFailed(operationId, phase); }
        catch (Exception) { /* 诊断提供器也不能改变执行结果。 */ }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "操作观察未持久化：OperationId={OperationId}，Phase={Phase}；操作日志已降级。")]
    private partial void LogCaptureFailed(Guid operationId, string phase);
}
