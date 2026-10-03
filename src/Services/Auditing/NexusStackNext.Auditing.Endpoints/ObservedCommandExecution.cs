using System.Diagnostics;
using System.Reflection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed class ObservedCommandExecution(OperationObservationWriter writer,
    OperationCaptureOptions options, IClock clock, OperationExecutionContext context,
    ICurrentUser? currentUser = null) : ICommandExecution, IExecutionContext
{
    public bool IsSystem => context.IsSystem;

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
        await writer.WriteAsync(started).ConfigureAwait(false);
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
            await writer.WriteAsync(started with
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
