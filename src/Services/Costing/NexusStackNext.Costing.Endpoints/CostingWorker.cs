using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;

namespace NexusStackNext.Costing.Endpoints;

internal sealed class CostingStartupCheck(CostingConnection connection) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await CostingDatabase.IsReadyAsync(connection.Value, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Costing 数据库不可用或需要迁移；检查 ConnectionStrings:Costing 并先运行 migrate-costing。");
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed partial class CostingWorker(IServiceScopeFactory scopes, CostingTaskOptions options, ILogger<CostingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CostingWorkLease? lease = null;
            var failed = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                lease = (await sender.SendAsync(new ClaimCostingWork(), stoppingToken).ConfigureAwait(false)).Value;
                if (lease is not null)
                {
                    var completed = await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch), stoppingToken).ConfigureAwait(false);
                    if (completed.IsFailure)
                    {
                        WorkRefused(logger, lease.TaskId, lease.Epoch, completed.Error.Code);
                        failed = true;
                    }
                    else { WorkCompleted(logger, lease.TaskId, lease.Epoch, completed.Value); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // 数据库或计算异常不写入载荷、连接配置或任务错误正文；记录稳定标识用于追踪。
                WorkFailed(logger, lease?.TaskId);
                failed = true;
            }
            if (failed && lease is not null)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ISender>()
                        .SendAsync(new FailCostingWork(lease.TaskId, lease.Epoch), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    FailureRecordFailed(logger, lease.TaskId);
                }
            }
            try { await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Costing task {TaskId} epoch {Epoch}: committed={Committed}")]
    private static partial void WorkCompleted(ILogger logger, Guid taskId, long epoch, bool committed);
    [LoggerMessage(2, LogLevel.Warning, "Costing worker failed for task {TaskId}; durable lease recovery remains active.")]
    private static partial void WorkFailed(ILogger logger, Guid? taskId);
    [LoggerMessage(3, LogLevel.Warning, "Costing task {TaskId} failure could not be recorded; waiting for lease expiry.")]
    private static partial void FailureRecordFailed(ILogger logger, Guid taskId);
    [LoggerMessage(4, LogLevel.Warning, "Costing task {TaskId} epoch {Epoch} refused with {ErrorCode}; durable retry recovery remains active.")]
    private static partial void WorkRefused(ILogger logger, Guid taskId, long epoch, string errorCode);
}
