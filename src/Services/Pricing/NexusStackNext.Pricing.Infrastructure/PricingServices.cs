using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>显式装配 Pricing 的 PostgreSQL 命令与查询适配器。</summary>
public static class PricingServices
{
    /// <summary>注册持久化定价模块。</summary>
    /// <param name="services">容器。</param>
    /// <param name="connectionString">所属数据库的连接配置。</param>
    /// <param name="options">有界执行策略。</param>
    /// <returns>容器。</returns>
    public static IServiceCollection AddPricingPostgres(this IServiceCollection services, string connectionString, PricingTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var policy = options ?? new PricingTaskOptions();
        policy.Validate();
        services.AddSingleton(policy);
        services.AddScoped(_ => PricingDatabase.CreateContext(connectionString));
        services.AddScoped<IIntegrationEventProcessor, PricingCostIngestion>();
        services.AddScoped<ICommandHandler<UpdatePricingFee, RecalculationStatus>, PricingFeeCommands>();
        services.AddScoped<ICommandHandler<UpdatePricingCost, RecalculationStatus>, PricingCommands>();
        services.AddScoped<IQueryHandler<GetRecalculation, RecalculationStatus>, PricingCommands>();
        services.AddScoped<IQueryHandler<GetPriceQuote, PriceQuoteView>, PricingCommands>();
        services.AddScoped<ICommandHandler<ClaimPricingWork, PricingWorkLease?>, PricingExecution>();
        services.AddScoped<ICommandHandler<CompletePricingWork, bool>, PricingExecution>();
        services.AddScoped<ICommandHandler<FailPricingWork, bool>, PricingExecution>();
        services.AddScoped<ICommandHandler<RetryPricingWork, RecalculationStatus>, PricingExecution>();
        return services;
    }
}

internal sealed class PricingCommands(PricingDbContext database) : ICommandHandler<UpdatePricingCost, RecalculationStatus>,
    IQueryHandler<GetRecalculation, RecalculationStatus>, IQueryHandler<GetPriceQuote, PriceQuoteView>
{
    public async Task<Result<RecalculationStatus>> HandleAsync(UpdatePricingCost command, CancellationToken cancellationToken = default)
    {
        if (command.ItemId == Guid.Empty || command.RequestId == Guid.Empty || command.ExpectedVersion < 0
            || !PriceQuote.IsValidInput(command.Cost, command.FeeRate))
        {
            return Result.Failure<RecalculationStatus>(new Error("pricing.invalid_input", "标识、版本或输入无效。"));
        }

        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // 两个稳定命名空间：先序列化同请求的重试，再序列化同定价对象的输入更新。
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-request/" + command.RequestId}, 0))", cancellationToken).ConfigureAwait(false);
        var existing = await database.Tasks.Include(x => x.History).SingleOrDefaultAsync(x => x.TaskId == command.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Matches(command) ? Result.Success(existing.ToStatus())
                : Result.Failure<RecalculationStatus>(new Error("pricing.request_conflict", "请求标识已用于不同内容。"));
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-item/" + command.ItemId}, 0))", cancellationToken).ConfigureAwait(false);
        var id = new PriceId(command.ItemId);
        var quote = await database.Quotes.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if ((quote?.Version ?? 0) != command.ExpectedVersion)
        {
            return Result.Failure<RecalculationStatus>(new Error("pricing.version_conflict", "定价对象已被修改。"));
        }

        if (quote is null)
        {
            quote = PriceQuote.Create(id, command.Cost, command.FeeRate).Value;
            database.Quotes.Add(quote);
        }
        else
        {
            var updated = quote.UpdateCost(command.Cost, command.FeeRate);
            if (updated.IsFailure) { return Result.Failure<RecalculationStatus>(updated.Error); }
        }

        var task = new RecalculationEntry
        {
            TaskId = command.RequestId,
            ItemId = id,
            ExpectedVersion = command.ExpectedVersion,
            Cost = command.Cost,
            FeeRate = command.FeeRate,
            InputRevision = quote.InputRevision,
        };
        database.Tasks.Add(task);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(task.ToStatus());
    }

    public async Task<Result<RecalculationStatus>> HandleAsync(GetRecalculation query, CancellationToken cancellationToken = default)
    {
        var entry = await database.Tasks.AsNoTracking().Include(x => x.History).SingleOrDefaultAsync(x => x.TaskId == query.TaskId, cancellationToken).ConfigureAwait(false);
        return entry is null ? Result.Failure<RecalculationStatus>(new Error("pricing.not_found", "任务不存在。"))
            : Result.Success(entry.ToStatus());
    }

    public async Task<Result<PriceQuoteView>> HandleAsync(GetPriceQuote query, CancellationToken cancellationToken = default)
    {
        if (query.ItemId == Guid.Empty) { return Result.Failure<PriceQuoteView>(new Error("pricing.not_found", "定价对象不存在。")); }
        var id = new PriceId(query.ItemId);
        var quote = await database.Quotes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        return quote is null ? Result.Failure<PriceQuoteView>(new Error("pricing.not_found", "定价对象不存在。"))
            : Result.Success(new PriceQuoteView(quote.Id.Value, quote.Version, quote.Cost, quote.FeeRate,
                quote.InputRevision, quote.CalculatedRevision, quote.BreakEvenPrice)
            { CostingRevision = quote.CostingRevision });
    }
}
