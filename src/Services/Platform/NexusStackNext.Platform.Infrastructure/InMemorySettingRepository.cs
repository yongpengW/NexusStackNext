using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;

namespace NexusStackNext.Platform.Infrastructure;

/// <summary>
/// 内存配置仓储。
/// <para>
/// 按 <b>Scope + Name 两段</b>做键，不做字符串前缀匹配——
/// 前缀会让 <c>identity</c> 命中 <c>identity-temp</c>，而那是个只有到线上才会发现的错。
/// </para>
/// <para>
/// 与 Identity 的内存仓储一样，它保存的是聚合实例本身，不做拷贝：
/// 同一个进程、同一个对象图。真实持久化不会有这个性质，这条差异写在这里。
/// </para>
/// </summary>
public sealed class InMemorySettingRepository : ISettingRepository
{
    private readonly ConcurrentDictionary<(string Scope, string Name), GlobalSetting> _settings = new();

    /// <inheritdoc />
    public Task<GlobalSetting?> FindAsync(SettingKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Task.FromResult(
            _settings.TryGetValue((key.Scope, key.Name), out var setting) ? setting : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GlobalSetting>> ListByScopeAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        IReadOnlyList<GlobalSetting> found =
        [
            .. _settings
                .Where(pair => string.Equals(pair.Key.Scope, scope, StringComparison.Ordinal))
                .Select(static pair => pair.Value)
                .OrderBy(static setting => setting.Key.Name, StringComparer.Ordinal)
        ];

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task AddAsync(GlobalSetting setting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);

        _settings[(setting.Key.Scope, setting.Key.Name)] = setting;
        return Task.CompletedTask;
    }
}

/// <summary>把 Platform 的端口接到内存适配器上。</summary>
public static class PlatformInfrastructureServiceCollectionExtensions
{
    /// <summary>注册内存配置存储与读写服务。<b>显式注册，不做程序集扫描</b>（架构不变量 8）。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddPlatformInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISettingRepository, InMemorySettingRepository>();
        services.AddScoped<SettingStore>();

        return services;
    }
}
