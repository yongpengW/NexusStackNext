using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed partial class AuditExportWorker(IServiceScopeFactory scopes, AuditExportOptions options, ILogger<AuditExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                var claimed = await sender.SendAsync(new ClaimAuditExport(), stoppingToken).ConfigureAwait(false);
                if (claimed.IsSuccess && claimed.Value is { } lease)
                { _ = await sender.SendAsync(new ProcessAuditExport(lease), stoppingToken).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { PollFailed(logger); }
            try { await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "调查导出本轮处理未完成，持久委托将按原身份恢复。")]
    private static partial void PollFailed(ILogger logger);
}
