using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>本人导出持久化装配；基础定价任务容器不隐式增加用户或成果端口依赖。</summary>
public static class PricingExportServices
{
    /// <summary>在已注册 Pricing PostgreSQL 的容器中显式启用本人导出命令、查询与执行协调。</summary>
    /// <param name="services">容器；启用方明确提供当前用户。</param>
    /// <param name="options">有限导出执行策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPricingExportPersistence(this IServiceCollection services, PricingExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var policy = options ?? new PricingExportOptions();
        policy.Validate();
        services.AddSingleton(policy);
        services.AddScoped<ICommandHandler<AcceptPricingExport, PricingExportStatus>, PricingExportCommands>();
        services.AddScoped<IQueryHandler<GetPricingExport, PricingExportStatus>, PricingExportCommands>();
        services.AddScoped<IQueryHandler<ListPricingExports, PricingExportPage>, PricingExportQueries>();
        services.AddScoped<ICommandHandler<CancelPricingExport, PricingExportStatus>, PricingExportCommands>();
        services.AddScoped<ICommandHandler<ClaimPricingExport, PricingExportLease?>, PricingExportExecution>();
        services.AddScoped<ICommandHandler<RenewPricingExport, PricingExportLease>, PricingExportExecution>();
        services.AddScoped<ICommandHandler<SelectPricingExportPublication, PricingExportStatus>, PricingExportExecution>();
        services.AddScoped<ICommandHandler<ClaimPricingExportPublication, PricingExportPublicationLease?>, PricingExportPublicationExecution>();
        services.AddScoped<ICommandHandler<CompletePricingExportPublication, PricingExportStatus>, PricingExportPublicationExecution>();
        services.AddScoped<ICommandHandler<FailPricingExport, bool>, PricingExportControl>();
        services.AddScoped<ICommandHandler<FailPricingExportPublication, bool>, PricingExportControl>();
        services.AddScoped<ICommandHandler<RenewPricingExportPublication, PricingExportPublicationLease>, PricingExportControl>();
        services.AddScoped<ICommandHandler<RetryPricingExport, PricingExportStatus>, PricingExportControl>();
        return services;
    }
}
