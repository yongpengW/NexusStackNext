using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class EfAuditEntryStore(AuditingDbContext context, IClock clock) : IAuditEntryStore
{
    public Task<Result<IngestionOutcome>> AcceptAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var fact = entry.Fact;
        var legacyFields = new
        {
            fact.MessageId,
            fact.EventName,
            fact.Source,
            fact.Action,
            fact.SubjectType,
            fact.SubjectId,
            fact.SubjectVersion,
            fact.ActorId,
            fact.OccurredAt,
            fact.TraceId,
            fact.CorrelationId,
        };
        // 旧消息继续使用原指纹；已接纳事实不能通过重投补造执行来源。
        object content = fact.Execution is null ? legacyFields : new { Fact = legacyFields, fact.Execution };
        if (fact.RelatedSubject is not null) { content = new { Fact = legacyFields, fact.Execution, fact.RelatedSubject }; }
        if (fact.CapacityPolicyChange is not null) { content = new { Fact = content, fact.CapacityPolicyChange }; }
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content)));
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

    public Task<AuditPage> QueryAsync(int page, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync(new AuditQuery(page, limit), cancellationToken);

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalize(clock.UtcNow);
        if (query.Validate().IsFailure) { throw new ArgumentException("事实查询条件无效。", nameof(query)); }
        var matches = context.Entries.AsNoTracking().Where(query.Predicate());
        var total = await matches.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var entries = await matches.OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
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
        services.AddScoped<IOperationObservationStore, EfOperationObservationStore>();
        services.AddScoped<AuditIngestion>();
        services.AddHostedService<AuditingDatabaseStartupCheck>();
        services.AddHealthChecks().AddCheck<AuditingDatabaseHealthCheck>("auditing-database", tags: [AuditingDiagnostics.HealthTag]);
        return services;
    }
}
