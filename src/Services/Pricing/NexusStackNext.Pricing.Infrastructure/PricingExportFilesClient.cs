using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Infrastructure.Exports;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>私有成果适配器的显式宿主装配。</summary>
public static class PricingExportFilesServices
{
    /// <summary>注册仅使用客户端证书认证的 HTTPS 交付。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">HTTPS 配置。</param>
    /// <param name="environmentName">宿主环境；决定是否允许测试撤销例外。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPricingExportFiles(this IServiceCollection services, PricingExportFilesOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        services.AddSingleton<IExportFiles>(_ => new GeneratedExportFilesClient(options, "pricing", environmentName));
        return services;
    }
}
