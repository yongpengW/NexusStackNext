using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.Files.Application;

namespace NexusStackNext.Files.Infrastructure;

internal sealed partial class FileRecoveryWorker(
    IServiceScopeFactory scopes,
    FileRecoveryOptions options,
    ILogger<FileRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FileRecovery>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { LogRetry(logger, error.GetType().Name); }
            try { await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "文件恢复轮次失败，将继续重试。故障类型：{FailureType}")]
    private static partial void LogRetry(ILogger logger, string failureType);
}
