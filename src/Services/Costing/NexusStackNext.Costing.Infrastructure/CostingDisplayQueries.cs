using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Infrastructure;

internal sealed class CostingQueryBudget(CostingCacheOptions options) : IDisposable
{
    public SemaphoreSlim Loads { get; } = new(options.MaxConcurrentLoads);
    public void Dispose() => Loads.Dispose();
}

internal sealed class CostingDisplayQueries(CostingDbContext database, CostingCacheOptions options,
    CostingQueryBudget budget, CostingRedisCache cache) : IQueryHandler<GetCostSheetDisplay, CostSheetView>
{
    public async Task<Result<CostSheetView>> HandleAsync(GetCostSheetDisplay query, CancellationToken cancellationToken = default)
    {
        if (query.ItemId == Guid.Empty) { return Missing(); }
        var cached = await cache.ReadOrAcquireAsync(query.ItemId, cancellationToken).ConfigureAwait(false);
        if (cached.Value is not null) { return Result.Success(cached.Value); }
        if (!await budget.Loads.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        { return Result.Failure<CostSheetView>(new Error("costing.query_busy", "成本展示查询繁忙，请稍后重试。")); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.LoadTimeout);
            var id = new CostId(query.ItemId);
            var sheet = await database.Sheets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, timeout.Token).ConfigureAwait(false);
            if (sheet is null) { return Missing(); }
            var value = new CostSheetView(sheet.Id.Value, sheet.Version, sheet.PurchaseCost, sheet.FreightCost,
                sheet.InputRevision, sheet.CalculatedRevision, sheet.UnitCost)
            { Audit = EntityAuditMetadata.From(sheet) };
            if (cached.Token is not null) { await cache.FillAsync(value, cached.Token, timeout.Token).ConfigureAwait(false); }
            return Result.Success(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result.Failure<CostSheetView>(new Error("costing.query_timeout", "成本展示查询超时，请稍后重试。")); }
        finally { budget.Loads.Release(); }
    }

    private static Result<CostSheetView> Missing() =>
        Result.Failure<CostSheetView>(new Error("costing.not_found", "成本核算对象不存在。"));
}
