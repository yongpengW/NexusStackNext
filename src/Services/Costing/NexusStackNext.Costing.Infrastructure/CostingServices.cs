using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Domain;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Costing.Infrastructure;

/// <summary>显式装配 Costing 的 PostgreSQL 命令与查询适配器。</summary>
public static class CostingServices
{
    /// <summary>显式接入本上下文账本的有界只读诊断。</summary>
    /// <param name="services">宿主服务。</param>
    /// <param name="options">独立读取预算。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddCostingFactCapacityReader(this IServiceCollection services, CommittedFactCapacityReadOptions? options = null)
        => services.AddCommittedFactCapacityReader<CostingDbContext>("costing", options);

    /// <summary>显式启动本上下文已交付审计事实副本的维护。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">本上下文保留与维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddCostingFactCleanup(this IServiceCollection services, CommittedFactCleanupOptions? options = null)
        => services.AddCommittedFactCleanup<CostingDbContext>("costing", NexusStackNext.Costing.Contracts.CostSheetCommittedV1.Name, options);

    /// <summary>注册持久化成本核算模块。</summary>
    /// <param name="services">容器。</param>
    /// <param name="connectionString">所属数据库的连接配置。</param>
    /// <param name="options">有界执行策略。</param>
    /// <returns>容器。</returns>
    public static IServiceCollection AddCostingPostgres(this IServiceCollection services, string connectionString, CostingTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var policy = options ?? new CostingTaskOptions();
        policy.Validate();
        services.AddSingleton(policy);
        services.TryAddSingleton<IIntegrationEventSerializer, SystemTextJsonIntegrationEventSerializer>();
        services.AddScoped<CostingCommittedFactInterceptor>();
        services.AddScoped(provider => CostingDatabase.CreateContext(connectionString, provider));
        services.AddScoped<IOutboxStore, EfOutboxStore<CostingDbContext>>();
        services.AddScoped<IQueryHandler<GetCostDelivery, CostDeliveryStatus>, CostDeliveryCommands>();
        services.AddScoped<ICommandHandler<RetryCostDelivery, CostDeliveryStatus>, CostDeliveryCommands>();
        services.AddScoped<ICommandHandler<UpdateCostInputs, CostCalculationStatus>, CostingCommands>();
        services.AddScoped<IQueryHandler<GetCostCalculation, CostCalculationStatus>, CostingCommands>();
        services.AddScoped<IQueryHandler<ListCostCalculations, CostCalculationPage>, CostingTaskQueries>();
        services.AddScoped<IQueryHandler<GetCostSheet, CostSheetView>, CostingCommands>();
        services.AddScoped<ICommandHandler<ClaimCostingWork, CostingWorkLease?>, CostingExecution>();
        services.AddScoped<ICommandHandler<CompleteCostingWork, bool>, CostingExecution>();
        services.AddScoped<ICommandHandler<FailCostingWork, bool>, CostingExecution>();
        services.AddScoped<ICommandHandler<RetryCostingWork, CostCalculationStatus>, CostingExecution>();
        services.AddScoped<ICommandHandler<CancelCostingWork, CostCalculationStatus>, CostingExecution>();
        services.AddScoped<ICommandHandler<RenewCostingWork, CostingWorkLease>, CostingExecution>();
        services.AddKeyedScoped<IIntegrationEventProcessor, ScheduledCostIngestion>(ScheduleTriggeredV1.Name);
        services.AddScoped<IQueryHandler<GetScheduledCostReceipt, ScheduledCostReceipt>, ScheduledCostIngestion>();
        return services;
    }
}

internal sealed class CostingCommands(CostingDbContext database, IExecutionContext execution) : ICommandHandler<UpdateCostInputs, CostCalculationStatus>,
    IQueryHandler<GetCostCalculation, CostCalculationStatus>, IQueryHandler<GetCostSheet, CostSheetView>
{
    public async Task<Result<CostCalculationStatus>> HandleAsync(UpdateCostInputs command, CancellationToken cancellationToken = default)
    {
        if (command.ItemId == Guid.Empty || command.RequestId == Guid.Empty || command.ExpectedVersion < 0 || command.DelaySeconds is < 0 or > 2_592_000
            || !CostSheet.IsValidInput(command.PurchaseCost, command.FreightCost))
        {
            return Result.Failure<CostCalculationStatus>(new Error("costing.invalid_input", "标识、版本或输入无效。"));
        }

        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // 两个稳定命名空间：先序列化同请求的重试，再序列化同成本核算对象的输入更新。
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-request/" + command.RequestId}, 0))", cancellationToken).ConfigureAwait(false);
        var existing = await database.Tasks.Include(x => x.History).SingleOrDefaultAsync(x => x.TaskId == command.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Matches(command) ? Result.Success(existing.ToStatus())
                : Result.Failure<CostCalculationStatus>(new Error("costing.request_conflict", "请求标识已用于不同内容。"));
        }
        if (await database.ScheduleReceipts.AnyAsync(x => x.OccurrenceId == command.RequestId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<CostCalculationStatus>(new Error("costing.request_conflict", "请求标识已经用于计划触发。"));
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"costing-item/" + command.ItemId}, 0))", cancellationToken).ConfigureAwait(false);
        var id = new CostId(command.ItemId);
        var sheet = await database.Sheets.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if ((sheet?.Version ?? 0) != command.ExpectedVersion)
        {
            return Result.Failure<CostCalculationStatus>(new Error("costing.version_conflict", "成本核算对象已被修改。"));
        }

        if (sheet is null)
        {
            sheet = CostSheet.Create(id, command.PurchaseCost, command.FreightCost).Value;
            database.Sheets.Add(sheet);
        }
        else
        {
            _ = sheet.UpdateCost(command.PurchaseCost, command.FreightCost);
        }

        var acceptedAt = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var task = new CostCalculationEntry
        {
            TaskId = command.RequestId,
            ExecutionOrigin = execution.Capture(),
            ItemId = id,
            ExpectedVersion = command.ExpectedVersion,
            PurchaseCost = command.PurchaseCost,
            FreightCost = command.FreightCost,
            InputRevision = sheet.InputRevision,
            DelaySeconds = command.DelaySeconds,
            CreatedAt = acceptedAt,
            AvailableAt = acceptedAt.AddSeconds(command.DelaySeconds),
        };
        database.Tasks.Add(task);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (CommittedFactCapacityFailure.Read(error, "costing", CostingErrors.AuditCapacityExceeded) is { } reason)
        {
            database.ChangeTracker.Clear();
            return Result.Failure<CostCalculationStatus>(reason);
        }
        return Result.Success(task.ToStatus());
    }

    public async Task<Result<CostCalculationStatus>> HandleAsync(GetCostCalculation query, CancellationToken cancellationToken = default)
    {
        var entry = await database.Tasks.AsNoTracking().Include(x => x.History).SingleOrDefaultAsync(x => x.TaskId == query.TaskId, cancellationToken).ConfigureAwait(false);
        return entry is null ? Result.Failure<CostCalculationStatus>(new Error("costing.not_found", "任务不存在。"))
            : Result.Success(entry.ToStatus());
    }

    public async Task<Result<CostSheetView>> HandleAsync(GetCostSheet query, CancellationToken cancellationToken = default)
    {
        if (query.ItemId == Guid.Empty) { return Result.Failure<CostSheetView>(new Error("costing.not_found", "成本核算对象不存在。")); }
        var id = new CostId(query.ItemId);
        var sheet = await database.Sheets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        return sheet is null ? Result.Failure<CostSheetView>(new Error("costing.not_found", "成本核算对象不存在。"))
            : Result.Success(new CostSheetView(sheet.Id.Value, sheet.Version, sheet.PurchaseCost, sheet.FreightCost,
                sheet.InputRevision, sheet.CalculatedRevision, sheet.UnitCost)
            { Audit = EntityAuditMetadata.From(sheet) });
    }
}
