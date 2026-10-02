using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingQueryBudget(PricingCacheOptions options) : IDisposable
{
    public SemaphoreSlim Loads { get; } = new(options.MaxConcurrentLoads);
    public void Dispose() => Loads.Dispose();
}

internal sealed class PricingQuoteQueries(PricingDbContext database, PricingCacheOptions options, PricingQueryBudget budget, PricingRedisCache cache)
    : IQueryHandler<GetPriceQuote, PriceQuoteView>
{
    public async Task<Result<PriceQuoteView>> HandleAsync(GetPriceQuote query, CancellationToken cancellationToken = default)
    {
        if (query.ItemId == Guid.Empty) { return Missing(); }
        var cached = await cache.ReadOrAcquireAsync(query.ItemId, cancellationToken).ConfigureAwait(false);
        if (cached.Value is not null) { return Result.Success(cached.Value); }
        if (!await budget.Loads.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        { return Result.Failure<PriceQuoteView>(new Error("pricing.query_busy", "定价查询繁忙，请稍后重试。")); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.LoadTimeout);
            var id = new PriceId(query.ItemId);
            var quote = await database.Quotes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, timeout.Token).ConfigureAwait(false);
            if (quote is null) { return Missing(); }
            var value = new PriceQuoteView(quote.Id.Value, quote.Version, quote.Cost, quote.FeeRate,
                quote.InputRevision, quote.CalculatedRevision, quote.BreakEvenPrice)
            { CostingRevision = quote.CostingRevision };
            if (cached.Token is not null) { await cache.FillAsync(value, cached.Token, timeout.Token).ConfigureAwait(false); }
            return Result.Success(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result.Failure<PriceQuoteView>(new Error("pricing.query_timeout", "定价查询超时，请稍后重试。")); }
        finally { budget.Loads.Release(); }
    }

    private static Result<PriceQuoteView> Missing() => Result.Failure<PriceQuoteView>(new Error("pricing.not_found", "定价对象不存在。"));
}
