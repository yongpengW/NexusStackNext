using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class EfAuditEntryStore(AuditingDbContext context) : IAuditEntryStore
{
    public Task<Result<IngestionOutcome>> AcceptAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry.Fact)));
        return context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var inbox = new EfInboxStore<AuditingDbContext>(context);
            var first = await inbox.TryBeginProcessingAsync(AuditIngestion.ConsumerName, entry.Fact.EventName,
                entry.Fact.MessageId, entry.RecordedAt, cancellationToken).ConfigureAwait(false);
            var receipt = await context.Inbox.SingleAsync(item => item.ConsumerName == AuditIngestion.ConsumerName
                && item.EventName == entry.Fact.EventName && item.MessageId == entry.Fact.MessageId, cancellationToken).ConfigureAwait(false);
            var fingerprint = context.Entry(receipt).Property<string?>(AuditingDbContext.PayloadHashProperty);
            if (!first)
            {
                return fingerprint.CurrentValue == hash ? Result.Success(IngestionOutcome.Duplicate)
                    : Result.Failure<IngestionOutcome>(AuditIngestion.MessageConflict);
            }
            fingerprint.CurrentValue = hash;
            context.Entries.Add(entry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(IngestionOutcome.Accepted);
        });
    }

    public async Task<AuditPage> QueryAsync(int page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var total = await context.Entries.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var entries = await context.Entries.AsNoTracking().OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id)
            .Skip((page - 1) * limit).Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new AuditPage(entries, total);
    }
}

/// <summary>审计 PostgreSQL 适配器的显式装配。</summary>
public static class AuditingPersistenceServiceCollectionExtensions
{
    /// <summary>注册拥有自身事务的持久审计存储。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">审计数据库配置。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddAuditingPostgresStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<AuditingDbContext>(options => options.UseNexusStackPostgres(connectionString, AuditingDbContext.SchemaName));
        services.AddScoped<IAuditEntryStore, EfAuditEntryStore>();
        services.AddScoped<AuditIngestion>();
        services.AddHostedService<AuditingDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<AuditingDatabaseHealthCheck>("auditing-database");
        return services;
    }
}
