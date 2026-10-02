using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed partial class PricingCacheInvalidationWorker(IServiceScopeFactory scopes, PricingRedisCache cache,
    PricingCacheOptions options, ILogger<PricingCacheInvalidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<PricingDbContext>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var pending = await database.CacheInvalidations.OrderBy(x => x.ItemId).ThenBy(x => x.Version)
                    .Take(100).ToListAsync(timeout.Token).ConfigureAwait(false);
                foreach (var entry in pending)
                {
                    if (!await cache.InvalidateAsync(entry.ItemId, timeout.Token).ConfigureAwait(false)) { break; }
                    // 只确认这一条意图，不能按 ItemId 清掉同时提交的更新。
                    database.CacheInvalidations.Remove(entry);
                    try { await database.SaveChangesAsync(timeout.Token).ConfigureAwait(false); }
                    catch (DbUpdateConcurrencyException) { database.Entry(entry).State = EntityState.Detached; }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            // EF 的无重试策略可能包装数据库异常，故障时仍要保留宿主和后续失效重试。
            catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or DbUpdateException
                or InvalidOperationException { InnerException: System.Data.Common.DbException })
            { InvalidationPending(logger); }
            try { await Task.Delay(options.InvalidationPollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(11, LogLevel.Warning, "Pricing cache invalidation is pending; the durable intent will be retried.")]
    private static partial void InvalidationPending(ILogger logger);
}
