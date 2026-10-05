using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
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
public sealed class InMemorySettingRepository : ISettingRepository, IOutboxStore, ISettingAuditDelivery, ICommittedFactCapacityReader
{
    private readonly IIntegrationEventSerializer _serializer;
    private readonly InMemoryCommittedFactCapacity _capacity;
    private readonly ConcurrentDictionary<(string Scope, string Name), GlobalSetting> _settings = new();
    private readonly Lock _writes = new();
    private Dictionary<Guid, OutboxEntry> _outbox = new();
    private readonly InMemoryFactDeliveryRecoveryStore _recovery;
    internal InMemoryFactCapacityPolicyStore Policy { get; }

    /// <summary>创建独立的开发存储与有限容量账本。</summary>
    /// <param name="serializer">最小事件序列化器。</param>
    /// <param name="capacity">本存储的事实保留上限。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    /// <param name="control">创建存储时的独立控制额度，不能在运行期修改。</param>
    /// <param name="recovery">独立恢复凭据池的开发启动上限。</param>
    public InMemorySettingRepository(IIntegrationEventSerializer serializer, MemoryCommittedFactCapacityOptions? capacity = null,
        CommittedFactCapacityWriteOptions? write = null, MemoryFactCapacityPolicyControlOptions? control = null,
        MemoryFactDeliveryRecoveryControlOptions? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
        _capacity = new(_writes, SettingCommittedV1.Name, capacity, write);
        _recovery = new(_capacity, () => _outbox,
            new("platform", SettingCommittedV1.Name, SettingFactCapacityPolicyChangedV1.Name,
                new(SettingAuditRecoveryErrors.Invalid, SettingAuditRecoveryErrors.Conflict, SettingAuditRecoveryErrors.RequestConflict,
                    SettingAuditRecoveryErrors.NotFound, SettingAuditRecoveryErrors.Exhausted, SettingAuditRecoveryErrors.Unmanaged, SettingAuditRecoveryErrors.DeliveryNotFound)), recovery);
        Policy = new(_writes, _capacity, () => _outbox, prepared => _outbox = prepared,
            new("platform", SettingFactCapacityPolicyChangedV1.From), _serializer, control);
    }

    internal ICommittedFactCleanup CreateFactCleanup(CommittedFactCleanupOptions options, IClock clock)
        => new InMemoryCommittedFactCleanup(_writes, () => _outbox, SettingCommittedV1.Name, options, clock, _capacity);

    /// <inheritdoc />
    public Task<Result<CommittedFactCapacitySnapshot>> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_capacity.Read("platform", cancellationToken));

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

        var pending = OutboxEntry.From(audit, _serializer);
        if (!_capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_settings.ContainsKey((setting.Key.Scope, setting.Key.Name)))
            {
                return Task.FromResult(Result.Failure(SettingStore.Conflict));
            }
            var snapshot = setting.Snapshot();
            if (!_capacity.TryCommit([pending], () =>
            {
                _outbox.Add(pending.Id, pending);
                _settings[(setting.Key.Scope, setting.Key.Name)] = snapshot;
            }, cancellationToken)) { return Task.FromResult(Result.Failure(SettingStore.AuditCapacityExhausted)); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(GlobalSetting setting, long originalVersion, SettingCommittedV1? audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = audit is null ? null : OutboxEntry.From(audit, _serializer);
        if (!_capacity.TryEnter(out var scope, cancellationToken)) { return Task.FromResult(Result.Failure(CommittedFactCapacityErrors.Busy)); }
        using (scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (setting.Key.Scope, setting.Key.Name);
            if (!_settings.TryGetValue(key, out var current) || current.Version != originalVersion)
            {
                return Task.FromResult(Result.Failure(SettingStore.Conflict));
            }
            var snapshot = setting.Snapshot();
            if (!_capacity.TryCommit(pending is null ? [] : [pending], () =>
            {
                if (pending is not null) { _outbox.Add(pending.Id, pending); }
                _settings[key] = snapshot;
            }, cancellationToken)) { return Task.FromResult(Result.Failure(SettingStore.AuditCapacityExhausted)); }
            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_capacity.Enter(cancellationToken))
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }
    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.MarkDelivered(now), cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, long expectedRetryRevision, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task<bool> UpdateAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (_capacity.Enter(cancellationToken))
        {
            if (!_outbox.TryGetValue(id, out var entry)) { return Task.FromResult(false); }
            var updated = update(entry);
            _outbox[id] = updated;
            return Task.FromResult(updated != entry);
        }
    }

    /// <inheritdoc />
    public Task<Result<FactDeliveryState>> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _recovery.GetAsync(messageId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FactDeliveryState>> ListAsync(string state, int limit, CancellationToken cancellationToken = default)
        => _recovery.ListAsync(state, limit, cancellationToken);

    /// <inheritdoc />
    public Task<int> CleanupRecoveriesAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
        => _recovery.CleanupRecoveriesAsync(batchSize, now, cancellationToken);

    /// <inheritdoc />
    public Task<Result<FactDeliveryRecoveryCapacity>> ReadRecoveryCapacityAsync(CancellationToken cancellationToken = default)
        => _recovery.ReadRecoveryCapacityAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Result<FactDeliveryRecoveryReceipt>> RecoverAsync(FactDeliveryRecoveryRequest request, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution, CancellationToken cancellationToken = default)
        => _recovery.RecoverAsync(request, actorId, occurredAt, execution, cancellationToken);

    /// <inheritdoc />
    public Task<Result<FactDeliveryRecoveryReceipt>> GetRecoveryAsync(Guid requestId, CancellationToken cancellationToken = default)
        => _recovery.GetRecoveryAsync(requestId, cancellationToken);
}

/// <summary>把 Platform 的端口接到内存适配器上。</summary>
public static class PlatformInfrastructureServiceCollectionExtensions
{
    /// <summary>显式接入本上下文内存事实副本的维护。</summary>
    /// <param name="services">容器。</param>
    /// <param name="options">维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPlatformMemoryFactCleanup(this IServiceCollection services, CommittedFactCleanupOptions? options = null)
        => services.AddCommittedFactCleanup(OutboxKey, (provider, policy) => provider.GetRequiredService<InMemorySettingRepository>()
            .CreateFactCleanup(policy, provider.GetRequiredService<IClock>()), options);

    /// <summary>宿主装配时显式选择 Platform 的 Outbox。</summary>
    public const string OutboxKey = "platform";
    /// <summary>注册内存配置存储与读写服务。<b>显式注册，不做程序集扫描</b>（架构不变量 8）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="capacity">显式开发存储的事实保留上限。</param>
    /// <param name="write">每次共用写锁获取的等待预算。</param>
    /// <param name="control">所属 Memory 控制池的启动上限。</param>
    /// <param name="recovery">所属 Memory 恢复凭据池的启动上限。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddPlatformInMemoryStorage(this IServiceCollection services, MemoryCommittedFactCapacityOptions? capacity = null,
        CommittedFactCapacityWriteOptions? write = null, MemoryFactCapacityPolicyControlOptions? control = null,
        MemoryFactDeliveryRecoveryControlOptions? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var policy = capacity ?? new();
        policy.Validate();
        var budget = write ?? new();
        budget.Validate();
        var controlLimits = control ?? new();
        controlLimits.Validate();
        var recoveryLimits = recovery ?? new();
        recoveryLimits.Validate();
        services.AddSingleton(provider => new InMemorySettingRepository(provider.GetRequiredService<IIntegrationEventSerializer>(), policy, budget, controlLimits, recoveryLimits));
        services.AddSingleton<ISettingRepository>(provider => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyStore>("platform", (provider, _) => provider.GetRequiredService<InMemorySettingRepository>().Policy);
        services.AddKeyedSingleton<ICommittedFactCapacityPolicyCleanup>("platform", (provider, _) => provider.GetRequiredService<InMemorySettingRepository>().Policy);
        services.AddKeyedSingleton<IOutboxStore>(OutboxKey, (provider, _) => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddKeyedSingleton<ICommittedFactCapacityReader>(OutboxKey, (provider, _) => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddSingleton<ISettingAuditDelivery>(provider => provider.GetRequiredService<InMemorySettingRepository>());
        services.AddScoped<SettingStore>();

        return services;
    }
}
