using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Endpoints;

internal sealed partial class CostBatchWorker(IServiceScopeFactory scopes, CostingTaskOptions options, ILogger<CostBatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CostBatchLease? lease = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                lease = (await sender.SendAsync(new ClaimCostBatch(), stoppingToken).ConfigureAwait(false)).Value;
                if (lease is not null)
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var segment = await sender.SendAsync(new ExecuteCostBatchSegment(lease.BatchId, lease.Epoch), stoppingToken).ConfigureAwait(false);
                        if (segment.IsFailure || !segment.Value) { break; }
                        var current = (await sender.QueryAsync(new GetCostBatch(lease.BatchId), stoppingToken).ConfigureAwait(false)).Value;
                        if (current.State != "Running" || current.Epoch != lease.Epoch) { break; }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                BatchFailed(logger, lease?.BatchId);
                if (lease is not null)
                {
                    try
                    {
                        await using var recovery = scopes.CreateAsyncScope();
                        await recovery.ServiceProvider.GetRequiredService<ISender>()
                            .SendAsync(new FailCostBatch(lease.BatchId, lease.Epoch), stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested) { BatchFailed(logger, lease.BatchId); }
                }
            }
            try { await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(5, LogLevel.Warning, "Costing batch {BatchId} failed; persistent lease recovery remains active.")]
    private static partial void BatchFailed(ILogger logger, Guid? batchId);
}
