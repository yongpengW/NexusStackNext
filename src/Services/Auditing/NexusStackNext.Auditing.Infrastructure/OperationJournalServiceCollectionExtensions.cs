using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>来源操作日志的显式存储装配；不启动中央 Auditing，也不连接业务上下文。</summary>
public static class OperationJournalServiceCollectionExtensions
{
    /// <summary>来源日志的命名 Outbox；宿主可用它显式接入现有投递器。</summary>
    public const string OutboxKey = "auditing.operation-journal";

    /// <summary>注册独立 EF 日志、启动检查与运行健康检查；不会执行迁移。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">来源日志数据库配置，不参与业务环境事务。</param>
    /// <param name="capacity">存储容量，省略时使用有界默认值。</param>
    /// <param name="cleanup">已交付记录清理政策。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddOperationJournalPostgresStorage(this IServiceCollection services, string connectionString,
        OperationJournalCapacityOptions? capacity = null, OperationJournalCleanupOptions? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        capacity ??= new();
        capacity.Validate();
        services.AddSingleton(capacity);
        cleanup ??= new();
        cleanup.Validate();
        services.AddSingleton(cleanup);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<OperationJournalStatus>();
        services.AddDbContextFactory<OperationJournalDbContext>(options => OperationJournalDatabase.Configure(options, connectionString));
        services.AddScoped<IOperationJournal, EfOperationJournal>();
        services.AddScoped<IOperationJournalMaintenance, EfOperationJournal>();
        services.AddKeyedScoped<IOutboxStore>(OutboxKey, (provider, _) => new OperationJournalOutboxStore(
            new EfOutboxStore<OperationJournalDbContext>(provider.GetRequiredService<OperationJournalDbContext>())));
        services.AddHostedService<OperationJournalStartupCheck>();
        services.AddHealthChecks().AddCheck<OperationJournalHealthCheck>("operation-journal", tags: [AuditingDiagnostics.HealthTag]);
        return services;
    }

    /// <summary>显式注册开发测试内存日志；进程退出后记录丢失，环境限制由宿主配置入口执行。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="capacity">存储容量，省略时使用有界默认值。</param>
    /// <param name="cleanup">已交付记录清理政策。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddOperationJournalMemoryStorage(this IServiceCollection services, OperationJournalCapacityOptions? capacity = null,
        OperationJournalCleanupOptions? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        capacity ??= new();
        capacity.Validate();
        services.AddSingleton(capacity);
        cleanup ??= new();
        cleanup.Validate();
        services.AddSingleton(cleanup);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<OperationJournalStatus>();
        services.AddSingleton<InMemoryOperationJournal>();
        services.AddSingleton<IOperationJournal>(provider => provider.GetRequiredService<InMemoryOperationJournal>());
        services.AddSingleton<IOperationJournalMaintenance>(provider => provider.GetRequiredService<InMemoryOperationJournal>());
        services.AddKeyedSingleton<IOutboxStore>(OutboxKey, (provider, _) =>
            new OperationJournalOutboxStore(provider.GetRequiredService<InMemoryOperationJournal>()));
        services.AddHealthChecks().AddCheck<InMemoryOperationJournalHealthCheck>("operation-journal", tags: [AuditingDiagnostics.HealthTag]);
        return services;
    }
}
