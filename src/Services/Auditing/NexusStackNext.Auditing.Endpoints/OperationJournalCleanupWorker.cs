using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed partial class OperationJournalCleanupWorker(IServiceScopeFactory scopes, OperationJournalCleanupOptions options,
    OperationJournalStatus status, ILogger<OperationJournalCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            budget.CancelAfter(options.Timeout);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
                await maintenance.CleanupDeliveredAsync(budget.Token).ConfigureAwait(false);
                await maintenance.CleanupRecoveryRecordsAsync(budget.Token).ConfigureAwait(false);
                status.ReportCleanupSuccess();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                status.ReportCleanupFailure();
                try { LogCleanupFailed(); }
                catch (Exception) { /* 诊断提供器不能停止清理循环；不输出原始异常。 */ }
            }
            try { await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "来源操作日志清理失败，将在下一轮重试；未释放未提交的额度。")]
    private partial void LogCleanupFailed();
}
