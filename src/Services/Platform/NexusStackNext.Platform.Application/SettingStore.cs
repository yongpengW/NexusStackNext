using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Platform.Domain.Settings;

namespace NexusStackNext.Platform.Application;

/// <summary>配置仓储端口。适配器可以是 EF Core（生产）或内存（开发与测试）。</summary>
public interface ISettingRepository
{
    /// <summary>按键查找。</summary>
    /// <param name="key">配置键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回聚合，否则 <c>null</c>。</returns>
    Task<GlobalSetting?> FindAsync(SettingKey key, CancellationToken cancellationToken = default);

    /// <summary>按分组列出。<b>按段比较，不做前缀匹配</b>——前缀会让 <c>identity</c> 命中 <c>identity-temp</c>。</summary>
    /// <param name="scope">分组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该分组下的配置项。</returns>
    Task<IReadOnlyList<GlobalSetting>> ListByScopeAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>保存新配置项。</summary>
    /// <param name="setting">配置聚合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(GlobalSetting setting, CancellationToken cancellationToken = default);
}

/// <summary>
/// 读写配置。
/// <para>
/// <b>写是"不存在就创建、存在就改值"</b>——调用方不需要先问"这个键注册过没有"。
/// 那句问话与随后的写之间有竞态，而把一个键的首次写入变成两步操作，
/// 会让每个调用方都要处理"刚刚被别人建了"这种情况。
/// </para>
/// <para>
/// <b>"同值写入不发事件"由聚合保证</b>（<c>GlobalSetting.ChangeValue</c>）：
/// 它是别的上下文刷新缓存的唯一信号，抖动会让整个系统无谓地跟着抖。
/// </para>
/// </summary>
/// <param name="settings">配置仓储。</param>
/// <param name="ids">标识生成器——<b>由调用方注入，不在构造函数里取全局状态</b>（架构不变量 6）。</param>
/// <param name="clock">时钟。</param>
public sealed class SettingStore(ISettingRepository settings, IIdGenerator ids, IClock clock)
{
    /// <summary>读一个配置值。键不存在与键存在但没有值都返回 <c>null</c>。</summary>
    /// <param name="key">配置键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置值或 <c>null</c>。</returns>
    public async Task<string?> ReadAsync(SettingKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var setting = await settings.FindAsync(key, cancellationToken).ConfigureAwait(false);
        return setting?.Value;
    }

    /// <summary>写一个配置值。键不存在时创建。</summary>
    /// <param name="key">配置键。</param>
    /// <param name="value">新值；<c>null</c> 表示清空。</param>
    /// <param name="description">说明；<c>null</c> 表示不改。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或聚合拒绝的原因。</returns>
    public async Task<Result> WriteAsync(
        SettingKey key,
        string? value,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var setting = await settings.FindAsync(key, cancellationToken).ConfigureAwait(false);

        if (setting is null)
        {
            // 首次写入即创建：初值直接带上，不先建后改。
            setting = GlobalSetting.Create(new SettingId(ids.NextId()), key, value, description);
            await settings.AddAsync(setting, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }

        if (description is not null)
        {
            setting.Describe(description);
        }

        return setting.ChangeValue(value, clock.UtcNow);
    }

    /// <summary>列出一个分组下的全部配置。</summary>
    /// <param name="scope">分组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该分组下的配置项。</returns>
    public Task<IReadOnlyList<GlobalSetting>> ListByScopeAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return settings.ListByScopeAsync(scope, cancellationToken);
    }
}
