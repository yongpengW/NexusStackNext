using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>开发测试适配器：同一把锁原子登记事实身份与记录，读取返回不可变事实。</summary>
public sealed class InMemoryAuditEntryStore : IAuditEntryStore
{
    private readonly Dictionary<(string EventName, Guid MessageId), AuditEntry> _entries = new();
    private readonly Lock _writes = new();

    /// <inheritdoc />
    public Task<Result<IngestionOutcome>> AcceptAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            var key = (entry.Fact.EventName, entry.Fact.MessageId);
            if (_entries.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing.Fact == entry.Fact ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(AuditIngestion.MessageConflict));
            }
            _entries.Add(key, entry);
            return Task.FromResult(Result.Success(IngestionOutcome.Accepted));
        }
    }

    /// <inheritdoc />
    public Task<AuditPage> QueryAsync(int page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writes)
        {
            return Task.FromResult(new AuditPage(_entries.Values.OrderByDescending(static entry => entry.RecordedAt).ThenByDescending(static entry => entry.Id.Value)
                .Skip((page - 1) * limit).Take(limit).ToArray(), _entries.Count));
        }
    }
}

/// <summary>Auditing 开发测试存储的显式装配。</summary>
public static class AuditingInfrastructureServiceCollectionExtensions
{
    /// <summary>注册内存审计存储；进程重启会清空。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddAuditingInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAuditEntryStore, InMemoryAuditEntryStore>();
        services.AddSingleton<IOperationObservationStore, InMemoryOperationObservationStore>();
        services.AddScoped<AuditIngestion>();
        return services;
    }
}
