using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>开发测试适配器：同一把锁原子登记事实身份与记录，读取返回不可变事实。</summary>
/// <param name="clock">默认调查窗口的时钟。</param>
/// <param name="capacity">本实例有限接纳配置。</param>
public sealed class InMemoryAuditEntryStore(IClock clock, AuditStorageCapacityOptions? capacity = null) : IAuditEntryStore
{
    private readonly AuditStorageCapacityOptions _capacity = (capacity ?? new()).Validate();
    private readonly Dictionary<(string EventName, Guid MessageId), AuditEntry> _entries = new();
    private readonly Lock _writes = new();

    /// <inheritdoc />
    public Task<Result<IngestionOutcome>> AcceptAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        using (AuditMemoryWriteLock.Enter(_writes, _capacity.WaitTimeoutMilliseconds, cancellationToken))
        {
            var key = (entry.Fact.EventName, entry.Fact.MessageId);
            if (_entries.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing.Fact == entry.Fact ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(AuditIngestion.MessageConflict));
            }
            if (_entries.Count >= _capacity.MaxFacts) { throw new AuditStorageUnavailableException(true); }
            _entries.Add(key, entry);
            return Task.FromResult(Result.Success(IngestionOutcome.Accepted));
        }
    }

    internal AuditStoragePoolCapacity ReadCapacity(CancellationToken cancellationToken)
    {
        using var scope = AuditMemoryWriteLock.Enter(_writes, _capacity.WaitTimeoutMilliseconds, cancellationToken);
        return new(_entries.Count, _capacity.MaxFacts);
    }

    /// <inheritdoc />
    public Task<AuditPage> QueryAsync(int page, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync(new AuditQuery(page, limit), cancellationToken);

    /// <inheritdoc />
    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalize(clock.UtcNow);
        if (query.Validate().IsFailure) { throw new ArgumentException("事实查询条件无效。", nameof(query)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var matches = _entries.Values.AsQueryable().Where(query.Predicate()).ToArray();
            return Task.FromResult(new AuditPage(matches.OrderByDescending(static entry => entry.RecordedAt).ThenByDescending(static entry => entry.Id.Value)
                .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToArray(), matches.LongLength));
        }
    }
}

/// <summary>Auditing 开发测试存储的显式装配。</summary>
public static class AuditingInfrastructureServiceCollectionExtensions
{
    /// <summary>注册内存审计存储；进程重启会清空。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="capacity">本实例有限接纳配置。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddAuditingInMemoryStorage(this IServiceCollection services, AuditStorageCapacityOptions? capacity = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton((capacity ?? new()).Validate());
        services.AddSingleton<InMemoryAuditEntryStore>();
        services.AddSingleton<IAuditEntryStore>(provider => provider.GetRequiredService<InMemoryAuditEntryStore>());
        services.AddSingleton<InMemoryOperationObservationStore>();
        services.AddSingleton<IOperationObservationStore>(provider => provider.GetRequiredService<InMemoryOperationObservationStore>());
        services.AddSingleton<IAuditStorageCapacityReader, InMemoryAuditCapacityReader>();
        services.AddScoped<AuditIngestion>();
        return services;
    }
}
