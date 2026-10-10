using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed partial class OperationObservationRetentionWorker(IServiceScopeFactory scopes, IClock clock,
    OperationObservationRetentionOptions options, ILogger<OperationObservationRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var observations = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
                await observations.DeleteExpiredAsync(clock.UtcNow - options.ObservationRetention, options.BatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogCleanupFailed(); }
            try { await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "中央操作观察清理未完成，将在下一轮重试。")]
    private partial void LogCleanupFailed();
}
