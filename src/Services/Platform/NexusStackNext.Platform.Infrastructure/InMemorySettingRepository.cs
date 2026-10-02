using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Domain;
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
/// 读取返回独立快照，提交按读取版本比较并替换。开发内存模式也不能让先前读取的
/// 对象被其他请求偷偷改动，或让两个旧版本写入都成功。
/// </para>
/// </summary>
public sealed class InMemorySettingRepository : ISettingRepository
{
    private readonly ConcurrentDictionary<(string Scope, string Name), GlobalSetting> _settings = new();
    private readonly Lock _writes = new();

    /// <inheritdoc />
    public Task<GlobalSetting?> FindAsync(SettingKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _settings.TryGetValue((key.Scope, key.Name), out var setting) ? setting.Snapshot() : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GlobalSetting>> ListByScopeAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<GlobalSetting> found =
        [
            .. _settings
                .Where(pair => string.Equals(pair.Key.Scope, scope, StringComparison.Ordinal))
                .Select(static pair => pair.Value.Snapshot())
                .OrderBy(static setting => setting.Key.Name, StringComparer.Ordinal)
        ];

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<Result> AddAsync(GlobalSetting setting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_settings.TryAdd((setting.Key.Scope, setting.Key.Name), setting.Snapshot())
            ? Result.Success() : Result.Failure(SettingStore.Conflict));
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(GlobalSetting setting, long originalVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var key = (setting.Key.Scope, setting.Key.Name);
            if (!_settings.TryGetValue(key, out var current) || current.Version != originalVersion)
            {
                return Task.FromResult(Result.Failure(SettingStore.Conflict));
            }
            _settings[key] = setting.Snapshot();
            return Task.FromResult(Result.Success());
        }
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
