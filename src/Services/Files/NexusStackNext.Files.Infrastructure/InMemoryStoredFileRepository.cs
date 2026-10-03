using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Infrastructure;

/// <summary>仅用于开发测试的文件元数据仓储；查询返回快照，提交时比较版本。</summary>
public sealed class InMemoryStoredFileRepository : IStoredFileRepository, IOutboxStore
{
    private readonly FilesMemoryState _state;
    private readonly StoredFileCommittedFacts _facts;
    private readonly IClock _clock;
    private readonly ICurrentUser? _currentUser;
    private readonly IExecutionContext? _execution;

    /// <summary>创建独立的开发存储；宿主使用共享状态和每次调用的身份。</summary>
    /// <param name="serializer">事实消息序列化。</param>
    /// <param name="clock">行审计及事实时钟。</param>
    public InMemoryStoredFileRepository(IIntegrationEventSerializer? serializer = null, IClock? clock = null)
    {
        _state = new FilesMemoryState();
        _clock = clock ?? new SystemClock();
        _facts = new StoredFileCommittedFacts(_clock, serializer ?? new SystemTextJsonIntegrationEventSerializer());
    }

    internal InMemoryStoredFileRepository(FilesMemoryState state, StoredFileCommittedFacts facts, IClock clock,
        ICurrentUser? currentUser, IExecutionContext? execution)
    {
        _state = state;
        _facts = facts;
        _clock = clock;
        _currentUser = currentUser;
        _execution = execution;
    }

    /// <inheritdoc />
    public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            return Task.FromResult(
                _state.Files.TryGetValue(id.Value, out var file) && !file.IsDeleted ? file.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    public Task<StoredFile?> FindDeletedAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            return Task.FromResult(_state.Files.TryGetValue(id.Value, out var file) && file.IsDeleted ? file.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredFile>> PendingDeletionsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            return Task.FromResult<IReadOnlyList<StoredFile>>(_state.Files.Values
                .Where(file => file.IsDeleted && file.BytesRemovedAt is null
                    && (file.NextCleanupAttemptAt is null || file.NextCleanupAttemptAt <= now))
                .OrderBy(file => file.NextCleanupAttemptAt != null).ThenBy(file => file.NextCleanupAttemptAt)
                .ThenBy(file => file.Id.Value).Take(limit).Select(file => file.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<ExecutionOrigin?> ReadDeletionOriginAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes) { return Task.FromResult(_state.DeletionOrigins.GetValueOrDefault(id.Value)); }
    }

    /// <inheritdoc />
    public Task SaveAsync(StoredFile file, long? originalVersion = null, ExecutionOrigin? deletionOrigin = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (deletionOrigin is not null && !deletionOrigin.IsValid()) { throw new ArgumentException("删除来源无效。", nameof(deletionOrigin)); }

        lock (_state.Writes)
        {
            if (file.StorageKey is not null && _state.Retired.Contains(file.StorageKey))
            {
                throw new InvalidOperationException("该文件写入已失效，不能发布元数据。");
            }
            var exists = _state.Files.TryGetValue(file.Id.Value, out var current);
            if (originalVersion is null ? exists : !exists || current!.Version != originalVersion.Value)
            {
                throw new FileMetadataConflictException();
            }
            // 整批构造与快照准备完成后再提交；序列化失败或取消不留下半批事实、状态或删除来源。
            var facts = _facts.Create(current, file);
            var snapshot = file.Snapshot();
            Stamp(snapshot, current);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var fact in facts) { _state.Outbox.Add(fact.Id, fact); }
            if (file.IsDeleted && current?.IsDeleted != true) { _state.DeletionOrigins[file.Id.Value] = deletionOrigin; }
            _state.Files[file.Id.Value] = snapshot;
            CopyAudit(snapshot, file);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RetireUnreferencedStorageAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            if (_state.Files.Values.Any(file => string.Equals(file.StorageKey, storageKey, StringComparison.Ordinal))) { return Task.FromResult(false); }
            _state.Retired.Add(storageKey);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ReadPendingAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(_state.Outbox.Values.Where(entry => entry.IsPending
                && (entry.NextAttemptAt is null || entry.NextAttemptAt <= now)).OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id).Take(batchSize).ToArray());
        }
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) => UpdateDeliveryAsync(id, entry => entry.MarkDelivered(now), cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkFailedAsync(Guid id, string failure, DateTimeOffset nextAttemptAt, long expectedRetryRevision, CancellationToken cancellationToken = default) => UpdateDeliveryAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.RecordFailure(failure, nextAttemptAt) : entry, cancellationToken);
    /// <inheritdoc />
    public Task<bool> MarkDeadLetteredAsync(Guid id, string failure, DateTimeOffset now, long expectedRetryRevision, CancellationToken cancellationToken = default) => UpdateDeliveryAsync(id, entry => entry.RetryRevision == expectedRetryRevision ? entry.MarkDeadLettered(failure, now) : entry, cancellationToken);

    private Task<bool> UpdateDeliveryAsync(Guid id, Func<OutboxEntry, OutboxEntry> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state.Writes)
        {
            if (!_state.Outbox.TryGetValue(id, out var before)) { return Task.FromResult(false); }
            var after = update(before);
            _state.Outbox[id] = after;
            return Task.FromResult(after != before);
        }
    }

    private void Stamp(StoredFile target, StoredFile? before)
    {
        var audit = (IAuditedEntity)target;
        var actor = _execution?.IsSystem == true ? null : _currentUser?.UserId;
        if (before is null)
        {
            audit.CreatedAt = _clock.UtcNow.ToUniversalTime();
            audit.CreatedBy = actor;
            audit.UpdatedAt = null;
            audit.UpdatedBy = null;
        }
        else
        {
            CopyAudit(before, target);
            if (before.Version == target.Version) { return; }
            audit.UpdatedAt = _clock.UtcNow.ToUniversalTime();
            audit.UpdatedBy = actor;
        }
    }

    private static void CopyAudit(StoredFile source, StoredFile target)
    {
        var audit = (IAuditedEntity)target;
        audit.CreatedAt = source.CreatedAt;
        audit.CreatedBy = source.CreatedBy;
        audit.UpdatedAt = source.UpdatedAt;
        audit.UpdatedBy = source.UpdatedBy;
    }
}

internal sealed class FilesMemoryState
{
    internal Dictionary<long, StoredFile> Files { get; } = [];
    internal Dictionary<long, ExecutionOrigin?> DeletionOrigins { get; } = [];
    internal HashSet<string> Retired { get; } = new(StringComparer.Ordinal);
    internal Dictionary<Guid, OutboxEntry> Outbox { get; } = [];
    internal Lock Writes { get; } = new();
}

/// <summary>Files 开发存储的显式装配。</summary>
public static class FilesMemoryServiceCollectionExtensions
{
    /// <summary>共享元数据与事实状态，每次调用独立解析当前身份和执行来源。</summary>
    /// <param name="services">服务容器。</param>
    /// <returns>原服务容器。</returns>
    public static IServiceCollection AddFilesInMemoryMetadata(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<FilesMemoryState>();
        services.AddScoped<StoredFileCommittedFacts>();
        services.AddScoped(provider => new InMemoryStoredFileRepository(provider.GetRequiredService<FilesMemoryState>(),
            provider.GetRequiredService<StoredFileCommittedFacts>(), provider.GetRequiredService<IClock>(),
            provider.GetService<ICurrentUser>(), provider.GetService<IExecutionContext>()));
        services.AddScoped<IStoredFileRepository>(provider => provider.GetRequiredService<InMemoryStoredFileRepository>());
        services.AddKeyedScoped<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey, (provider, _) => provider.GetRequiredService<InMemoryStoredFileRepository>());
        return services;
    }
}
