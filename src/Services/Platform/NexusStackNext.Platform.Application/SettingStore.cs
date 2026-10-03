using System.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Platform.Contracts;
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
    /// <param name="audit">与状态同事务保存的最小事实。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已提交，或稳定键冲突。</returns>
    Task<Result> AddAsync(GlobalSetting setting, SettingCommittedV1 audit, CancellationToken cancellationToken = default);

    /// <summary>原子保存已读取配置的值、说明和版本。</summary>
    /// <param name="setting">已修改的配置聚合。</param>
    /// <param name="originalVersion">应用修改前读到的版本。</param>
    /// <param name="audit">与状态同事务保存的事实；空操作不产生事实。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已提交，或读取版本已过期。</returns>
    Task<Result> SaveAsync(GlobalSetting setting, long originalVersion, SettingCommittedV1? audit, CancellationToken cancellationToken = default);
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
/// 是否公开为跨上下文通知由应用层的契约映射与真实消费者决定。
/// </para>
/// </summary>
/// <param name="settings">配置仓储。</param>
/// <param name="ids">标识生成器——<b>由调用方注入，不在构造函数里取全局状态</b>（架构不变量 6）。</param>
/// <param name="clock">时钟。</param>
/// <param name="currentUser">可信执行身份。</param>
/// <param name="execution">当前执行的安全关联；没有采集模块时可为空。</param>
public sealed class SettingStore(ISettingRepository settings, IIdGenerator ids, IClock clock, ICurrentUser currentUser,
    IExecutionContext? execution = null)
{
    /// <summary>当前版本或稳定键已被其他写入改变。</summary>
    public static readonly Error Conflict = new("platform.setting.conflict", "配置已被其他操作修改，请重新读取后再提交。");

    /// <summary>审计事实保留额度不足，本次业务变更没有提交。</summary>
    public static readonly Error AuditCapacityExhausted = new("platform.audit_capacity.exhausted", "审计事实存储容量不足，本次变更未提交，请稍后重试。");

    /// <summary>读取配置的值、说明与版本；未注册时返回空。</summary>
    /// <param name="key">配置键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置当前状态。</returns>
    public Task<GlobalSetting?> GetAsync(SettingKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return settings.FindAsync(key, cancellationToken);
    }

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
    /// <param name="expectedVersion">条件写的读取版本；0 要求尚未注册，省略则替换请求开始时读取的状态。</param>
    /// <returns>成功，或聚合拒绝的原因。</returns>
    public async Task<Result> WriteAsync(
        SettingKey key,
        string? value,
        string? description = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (expectedVersion < 0)
        {
            return Result.Failure(new Error("platform.setting.version", "配置版本不能为负数。"));
        }

        var setting = await settings.FindAsync(key, cancellationToken).ConfigureAwait(false);
        if (expectedVersion is { } expected && expected != (setting?.Version ?? 0))
        {
            return Result.Failure(Conflict);
        }

        if (setting is null)
        {
            // 首次写入即创建：初值直接带上，不先建后改。
            setting = GlobalSetting.Create(new SettingId(ids.NextId()), key, value, description);
            return await settings.AddAsync(setting, Fact(setting, "created"), cancellationToken).ConfigureAwait(false);
        }

        var originalVersion = setting.Version;
        var clearingValue = setting.Value is not null && value is null;
        if (description is not null)
        {
            setting.Describe(description);
        }

        var changed = setting.ChangeValue(value, clock.UtcNow);
        if (changed.IsSuccess)
        {
            return await settings.SaveAsync(setting, originalVersion,
                setting.Version == originalVersion ? null : Fact(setting, clearingValue ? "cleared" : "changed"), cancellationToken).ConfigureAwait(false);
        }
        return changed;
    }

    private SettingCommittedV1 Fact(GlobalSetting setting, string operation)
    {
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        return new SettingCommittedV1
        {
            Key = setting.Key.Value,
            Operation = operation,
            Version = setting.Version,
            ActorId = execution?.IsSystem == true ? null : currentUser.UserId,
            OccurredAt = clock.UtcNow,
            TraceId = trace,
            // 关联字段不是身份凭据；Actor 始终来自可信会话。
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        };
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
