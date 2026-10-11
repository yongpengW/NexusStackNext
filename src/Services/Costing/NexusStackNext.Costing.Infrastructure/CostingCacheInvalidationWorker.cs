using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed partial class CostingCacheInvalidationWorker(IServiceScopeFactory scopes, CostingRedisCache cache,
    CostingCacheOptions options, ILogger<CostingCacheInvalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<CostingDbContext>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var pending = await database.CacheInvalidations.OrderBy(x => x.ItemId).ThenBy(x => x.Version)
                    .Take(100).ToListAsync(timeout.Token).ConfigureAwait(false);
                foreach (var entry in pending)
                {
                    if (!await cache.InvalidateAsync(entry.ItemId, timeout.Token).ConfigureAwait(false)) { break; }
                    database.CacheInvalidations.Remove(entry);
                    try { await database.SaveChangesAsync(timeout.Token).ConfigureAwait(false); }
                    catch (DbUpdateConcurrencyException) { database.Entry(entry).State = EntityState.Detached; }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or DbUpdateException
                or InvalidOperationException { InnerException: System.Data.Common.DbException })
            { InvalidationPending(logger); }
            try { await Task.Delay(options.InvalidationPollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(11, LogLevel.Warning, "Costing cache invalidation is pending; the durable intent will be retried.")]
    private static partial void InvalidationPending(ILogger logger);
}
