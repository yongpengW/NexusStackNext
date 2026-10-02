using System.Diagnostics;
using System.Reflection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed partial class ObservedCommandExecution(IOperationJournal journal, OperationJournalStatus status,
    OperationCaptureOptions options, IClock clock, OperationExecutionContext context, ILogger<ObservedCommandExecution> logger,
    ICurrentUser? currentUser = null) : ICommandExecution, IExecutionContext
{
    public ExecutionOrigin? Capture()
    {
        var origin = context.Origin;
        if (origin is null || context.IsSystem || origin.InitiatorId is not null) { return origin; }
        var actor = currentUser?.UserId;
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl)) { actor = null; }
        return origin with { InitiatorId = actor };
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(object command, Func<Task<TResponse>> execute,
        CancellationToken cancellationToken = default) where TResponse : Result
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execute);
        if (context.IsSuppressed || context.Current is not null) { return await execute().ConfigureAwait(false); }
        if (command.GetType().GetCustomAttribute<CommandObservationSuppressionAttribute>() is { } suppression
            && !string.IsNullOrWhiteSpace(suppression.Reason)) { return await execute().ConfigureAwait(false); }
        var operationId = Guid.NewGuid();
        var name = command.GetType().Name;
        var actor = currentUser?.UserId;
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl)) { actor = null; }
        var started = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = operationId,
            Source = options.Source,
            Kind = "command",
            Phase = "started",
            ActorId = actor,
            OccurredAt = clock.UtcNow,
            TraceId = Activity.Current?.TraceId.ToString() ?? operationId.ToString("N"),
            Metadata = new OperationDetails
            {
                Action = name.Length <= 192 ? "command." + name : "command",
                ExecutionRole = "command",
                RootOperationId = operationId,
                RootSource = options.Source,
                InitiatorId = actor,
                TaskId = command is ITaskOperationCommand task && task.TaskId != Guid.Empty ? task.TaskId : null,
            },
        };
        var timer = Stopwatch.StartNew();
        await RecordAsync(started).ConfigureAwait(false);
        using var operationScope = context.Enter(new ExecutionOrigin(operationId, options.Source, operationId,
            options.Source, actor, started.TraceId));
        var outcome = "failed";
        try
        {
            var result = await execute().ConfigureAwait(false);
            outcome = result.IsFailure ? "rejected"
                : command.GetType().IsDefined(typeof(BackgroundWorkAcceptanceAttribute), inherit: false) ? "accepted" : "completed";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            operationScope.Dispose();
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

    private async Task RecordAsync(OperationObservedV1 observation)
    {
        using var timeout = new CancellationTokenSource(options.WriteTimeout);
        try
        {
            if ((await journal.AppendAsync(observation, timeout.Token).ConfigureAwait(false)).IsSuccess) { return; }
        }
        catch (Exception) { /* 观察故障不改写命令结果，也不输出可能含秘密的异常正文。 */ }
        status.ReportFailure();
        try { LogCaptureFailed(observation.OperationId, observation.Phase); }
        catch (Exception) { /* 诊断提供器故障同样不能改变原命令。 */ }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "操作观察未持久化：OperationId={OperationId}，Phase={Phase}；操作日志已降级。")]
    private partial void LogCaptureFailed(Guid operationId, string phase);
}
