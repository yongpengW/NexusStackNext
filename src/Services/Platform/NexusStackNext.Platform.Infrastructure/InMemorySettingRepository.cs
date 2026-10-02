using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
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
/// <param name="serializer">最小事件序列化器。</param>
public sealed class InMemorySettingRepository(IIntegrationEventSerializer serializer) : ISettingRepository, IOutboxStore, ISettingAuditDelivery
{
    private readonly ConcurrentDictionary<(string Scope, string Name), GlobalSetting> _settings = new();
    private readonly Lock _writes = new();
    private readonly Dictionary<Guid, OutboxEntry> _outbox = new();

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
    public Task<Result> AddAsync(GlobalSetting setting, SettingCommittedV1 audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = OutboxEntry.From(audit, serializer);
        lock (_writes)
        {
            if (!_settings.TryAdd((setting.Key.Scope, setting.Key.Name), setting.Snapshot()))
            {
                return Task.FromResult(Result.Failure(SettingStore.Conflict));
            }
            _outbox.Add(pending.Id, pending);
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(GlobalSetting setting, long originalVersion, SettingCommittedV1? audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = audit is null ? null : OutboxEntry.From(audit, serializer);
        lock (_writes)
        {
            var key = (setting.Key.Scope, setting.Key.Name);
            if (!_settings.TryGetValue(key, out var current) || current.Version != originalVersion)
            {
                return Task.FromResult(Result.Failure(SettingStore.Conflict));
            }
            _settings[key] = setting.Snapshot();
            if (pending is not null) { _outbox.Add(pending.Id, pending); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }
    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsDelivered ? entry : entry.MarkDelivered(now) with { DeadLetteredAt = null, NextAttemptAt = null }, cancellationToken);
    /// <inheritdoc />
    public Task MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsPending ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);
    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.IsPending ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task UpdateAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes) { if (_outbox.TryGetValue(id, out var entry)) { _outbox[id] = update(entry); } }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SettingAuditDelivery>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        if (state is not ("Pending" or "Delivered" or "DeadLettered")) { throw new ArgumentException("未知投递状态。", nameof(state)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult<IReadOnlyList<SettingAuditDelivery>>(_outbox.Values.OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id)
                .Select(SettingAuditDelivery.From).Where(entry => entry.State == state).Take(limit).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<Result<SettingAuditDelivery>> RetryAsync(Guid messageId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            _outbox.TryGetValue(messageId, out var entry);
            var retry = SettingAuditDelivery.Retry(entry, expectedDeadLetteredAt);
            if (retry.IsFailure)
            {
                return Task.FromResult(Result.Failure<SettingAuditDelivery>(retry.Error));
            }
            _outbox[messageId] = retry.Value;
            return Task.FromResult(Result.Success(SettingAuditDelivery.From(retry.Value)));
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

        services.AddSingleton<InMemorySettingRepository>();
        services.AddSingleton<ISettingRepository>(provider => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddSingleton<IOutboxStore>(provider => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddSingleton<ISettingAuditDelivery>(provider => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddScoped<SettingStore>();

        return services;
    }
}
