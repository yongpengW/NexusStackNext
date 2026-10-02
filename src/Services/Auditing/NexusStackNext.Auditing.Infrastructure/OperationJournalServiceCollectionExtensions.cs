using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
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
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddOperationJournalPostgresStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton<OperationJournalStatus>();
        services.AddDbContextFactory<OperationJournalDbContext>(options => OperationJournalDatabase.Configure(options, connectionString));
        services.AddScoped<IOperationJournal, EfOperationJournal>();
        services.AddKeyedScoped<IOutboxStore>(OutboxKey, (provider, _) => new OperationJournalOutboxStore(
            new EfOutboxStore<OperationJournalDbContext>(provider.GetRequiredService<OperationJournalDbContext>())));
        services.AddHostedService<OperationJournalStartupCheck>();
        services.AddHealthChecks().AddCheck<OperationJournalHealthCheck>("operation-journal", tags: [AuditingDiagnostics.HealthTag]);
        return services;
    }

    /// <summary>显式注册开发测试内存日志；进程退出后记录丢失，环境限制由宿主配置入口执行。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddOperationJournalMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OperationJournalStatus>();
        services.AddSingleton<InMemoryOperationJournal>();
        services.AddSingleton<IOperationJournal>(provider => provider.GetRequiredService<InMemoryOperationJournal>());
        services.AddKeyedSingleton<IOutboxStore>(OutboxKey, (provider, _) =>
            new OperationJournalOutboxStore(provider.GetRequiredService<InMemoryOperationJournal>().Outbox));
        services.AddHealthChecks().AddCheck<InMemoryOperationJournalHealthCheck>("operation-journal", tags: [AuditingDiagnostics.HealthTag]);
        return services;
    }
}
