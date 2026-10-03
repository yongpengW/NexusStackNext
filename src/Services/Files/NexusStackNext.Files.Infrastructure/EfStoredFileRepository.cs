using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure.Persistence;

namespace NexusStackNext.Files.Infrastructure;

internal sealed class EfStoredFileRepository(FilesDbContext context, StoredFileCommittedFacts facts) : IStoredFileRepository
{
    public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id && !file.IsDeleted, cancellationToken);

    public Task<StoredFile?> FindDeletedAsync(StoredFileId id, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id && file.IsDeleted, cancellationToken);

    public Task<ExecutionOrigin?> ReadDeletionOriginAsync(StoredFileId id, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().Where(file => file.Id == id && file.IsDeleted)
            .Select(file => EF.Property<ExecutionOrigin?>(file, FilesDbContext.DeletionOriginProperty))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> PendingDeletionsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
        await context.Files.AsNoTracking().Where(file => file.IsDeleted && file.BytesRemovedAt == null
            && (file.NextCleanupAttemptAt == null || file.NextCleanupAttemptAt <= now))
            .OrderBy(file => file.NextCleanupAttemptAt != null).ThenBy(file => file.NextCleanupAttemptAt)
            .ThenBy(file => file.Id).Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public Task SaveAsync(StoredFile file, long? originalVersion = null, ExecutionOrigin? deletionOrigin = null, CancellationToken cancellationToken = default) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(file);
        if (deletionOrigin is not null && !deletionOrigin.IsValid()) { throw new ArgumentException("删除来源无效。", nameof(deletionOrigin)); }
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (file.StorageKey is not null)
            {
                await LockStorageKeyAsync(file.StorageKey, cancellationToken).ConfigureAwait(false);
                if (await context.RetiredStorageKeys.AnyAsync(key => key.StorageKey == file.StorageKey, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("该文件写入已失效，不能发布元数据。");
                }
            }
            StoredFile? before = null;
            if (originalVersion is null) { context.Files.Add(file); }
            else
            {
                before = await context.Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == file.Id, cancellationToken).ConfigureAwait(false);
                if (before is null || before.Version != originalVersion.Value) { throw new FileMetadataConflictException(); }
                var entry = context.Attach(file);
                entry.State = EntityState.Modified;
                entry.Property(item => item.Version).OriginalValue = originalVersion.Value;
            }
            var origin = context.Entry(file).Property<ExecutionOrigin?>(FilesDbContext.DeletionOriginProperty);
            if (file.IsDeleted && before?.IsDeleted != true) { origin.CurrentValue = deletionOrigin; }
            else if (originalVersion is not null) { origin.IsModified = false; }
            context.Outbox.AddRange(facts.Create(before, file));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException) { throw new FileMetadataConflictException(); }
        finally { context.ChangeTracker.Clear(); }
    });

    public Task<bool> RetireUnreferencedStorageAsync(string storageKey, CancellationToken cancellationToken = default) =>
        context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (await context.Files.AnyAsync(file => file.StorageKey == storageKey, cancellationToken).ConfigureAwait(false)) { return false; }
        if (!await context.RetiredStorageKeys.AnyAsync(key => key.StorageKey == storageKey, cancellationToken).ConfigureAwait(false))
        {
            context.RetiredStorageKeys.Add(new RetiredStorageKey(storageKey));
        }
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { context.ChangeTracker.Clear(); }
    });

    private Task<int> LockStorageKeyAsync(string storageKey, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({storageKey}, 360036))", cancellationToken);
}

/// <summary>Files 持久元数据的显式装配。</summary>
public static class FilesPersistenceServiceCollectionExtensions
{
    /// <summary>Files 自有的提交事实 Outbox。</summary>
    public const string OutboxKey = "files";

    /// <summary>注册 Files 的 PostgreSQL 适配器。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">Files 的连接配置。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddFilesPostgresMetadata(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<FilesDbContext>((provider, options) => options
            .UseNexusStackPostgres(connectionString, FilesDbContext.SchemaName)
            .UseNexusStackInterceptors(provider));
        services.AddScoped<IStoredFileRepository, EfStoredFileRepository>();
        services.AddScoped<StoredFileCommittedFacts>();
        services.AddKeyedScoped<IOutboxStore, EfOutboxStore<FilesDbContext>>(OutboxKey);
        services.AddHostedService<FilesDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<FilesDatabaseHealthCheck>("files-database");
        return services;
    }
}
