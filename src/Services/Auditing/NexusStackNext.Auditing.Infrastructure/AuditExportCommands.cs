using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Exports;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class AuditExportCommands(AuditingDbContext database, ICurrentUser user, IExecutionContext execution) : ICommandHandler<AcceptAuditExport, AuditExportStatus>
{
    public async Task<Result<AuditExportStatus>> HandleAsync(AcceptAuditExport command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.UserId)) { return Failure("unauthenticated"); }
        if (command.RequestId == Guid.Empty || (command.Facts is null) == (command.Operations is null)) { return Failure("invalid"); }
        var facts = command.Facts is { } factQuery ? factQuery with { Page = 1, Limit = 100, From = factQuery.From?.ToUniversalTime(), To = factQuery.To?.ToUniversalTime() } : null;
        var operations = command.Operations is { } operationQuery ? operationQuery with { Page = 1, Limit = 100, From = operationQuery.From?.ToUniversalTime(), To = operationQuery.To?.ToUniversalTime() } : null;
        var canonical = JsonSerializer.Serialize(new { Version = 1, Facts = facts, Operations = operations });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        database.ChangeTracker.Clear();
        try
        {
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
                await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '5s';", budget.Token).ConfigureAwait(false);
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"auditing-export/" + user.UserId + "/" + command.RequestId.ToString("D")}, 0))", budget.Token).ConfigureAwait(false);
                var sameRequest = database.Exports.AsNoTracking().Where(x => x.OwnerId == user.UserId && x.RequestId == command.RequestId);
                var existing = await sameRequest.Select(x => x.CanonicalRequest).SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing == canonical ? Result.Success(await sameRequest.Select(AuditExportQueries.Projection).SingleAsync(budget.Token).ConfigureAwait(false))
                        : Failure("request_conflict");
                }
                var now = await database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(budget.Token).ConfigureAwait(false);
                facts = facts?.Normalize(now);
                operations = operations?.Normalize(now);
                var validation = facts?.Validate() ?? operations!.Validate();
                if (validation.IsFailure) { return Result.Failure<AuditExportStatus>(validation.Error); }
                if (operations is not null)
                {
                    var evidence = await OperationEvidenceQuery.Select(database, operations).Take(5001).ToArrayAsync(budget.Token).ConfigureAwait(false);
                    if (evidence.Length == 0) { return Failure("empty"); }
                    if (evidence.Length > 5000) { return Failure("too_many_rows"); }
                    var operationRows = evidence.Select(static x => new[]
                    {
                    "operation-observation", x.OperationId.ToString("D"), x.Source, x.Kind, x.Metadata?.Action ?? "", x.Metadata?.ExecutionRole ?? "",
                    x.Metadata?.SubjectType ?? "", x.Metadata?.SubjectId ?? "", x.ActorId ?? "", x.TraceId, x.Metadata?.CorrelationId ?? "",
                    x.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "", x.FinishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                    x.Outcome, x.HttpMethod ?? "", x.RouteTemplate ?? "", x.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "",
                    x.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "", x.Metadata?.RootOperationId?.ToString("D") ?? "", x.Metadata?.RootSource ?? "",
                    x.Metadata?.ParentOperationId?.ToString("D") ?? "", x.Metadata?.ParentSource ?? "", x.Metadata?.InitiatorId ?? "",
                    x.Metadata?.TaskId?.ToString("D") ?? "", x.Metadata?.TaskEpoch?.ToString(CultureInfo.InvariantCulture) ?? "",
                    x.Metadata?.SchedulePlanId?.ToString(CultureInfo.InvariantCulture) ?? "", x.Metadata?.ScheduleExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? "",
                    x.Metadata?.ScheduleDecisionId?.ToString("D") ?? "",
                }).ToArray();
                    return await SaveAsync("operations", operations.From!.Value, operations.To!.Value, operationRows).ConfigureAwait(false);
                }
                // One SELECT freezes all selected facts; no ordinary paging or cross-context table access.
                var observed = await database.Entries.AsNoTracking().Where(facts!.Predicate()).OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id)
                    .Take(5001).ToArrayAsync(budget.Token).ConfigureAwait(false);
                if (observed.Length == 0) { return Failure("empty"); }
                if (observed.Length > 5000) { return Failure("too_many_rows"); }
                var rows = observed.Select(static x => new[]
                {
                "committed-fact", x.Id.Value.ToString(CultureInfo.InvariantCulture), x.Fact.MessageId.ToString("D"),
                x.Fact.EventName, x.Fact.Source, x.Fact.Action, x.Fact.SubjectType, x.Fact.SubjectId,
                x.Fact.SubjectVersion.ToString(CultureInfo.InvariantCulture), x.Fact.ActorId ?? "",
                x.Fact.OccurredAt.ToString("O", CultureInfo.InvariantCulture), x.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                x.Fact.TraceId, x.Fact.CorrelationId, x.Fact.Execution?.OperationId.ToString("D") ?? "", x.Fact.Execution?.Source ?? "",
                x.Fact.Execution?.RootOperationId.ToString("D") ?? "", x.Fact.Execution?.RootSource ?? "", x.Fact.Execution?.InitiatorId ?? "",
                x.Fact.RelatedSubject?.Context ?? "", x.Fact.RelatedSubject?.Type ?? "", x.Fact.RelatedSubject?.Id ?? "",
            }).ToArray();
                return await SaveAsync("facts", facts.From!.Value, facts.To!.Value, rows).ConfigureAwait(false);

                async Task<Result<AuditExportStatus>> SaveAsync(string kind, DateTimeOffset from, DateTimeOffset to, string[][] snapshot)
                {
                    if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 8_388_608) { return Failure("snapshot_limit"); }
                    var accepted = AuditExport.Accept(new AuditExportId(Guid.NewGuid()), user.UserId!, command.RequestId, canonical, kind, now, now, from, to, snapshot);
                    if (accepted.IsFailure) { return Result.Failure<AuditExportStatus>(accepted.Error); }
                    database.Exports.Add(accepted.Value);
                    var origin = execution.Capture();
                    database.Entry(accepted.Value).Property<ExecutionOrigin?>("ExecutionOrigin").CurrentValue = origin?.IsValid() == true ? origin : null;
                    await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                    await database.Entry(accepted.Value).ReloadAsync(budget.Token).ConfigureAwait(false);
                    await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                    return Result.Success(Status(accepted.Value));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("unavailable"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure("unavailable"); }
        finally { database.ChangeTracker.Clear(); }
    }

    internal static AuditExportStatus Status(AuditExport export) => new(export.Id.Value, export.RequestId, export.Version, export.Kind, export.State,
        export.AcceptedAt, export.FrozenAt, export.From, export.To, export.RowCount, EntityAuditMetadata.From(export))
    {
        Epoch = export.Epoch,
        Attempts = export.Attempts,
        LeaseUntil = export.LeaseUntil,
        FileId = export.FileId,
        ArtifactDigest = export.ArtifactDigest,
        ArtifactLength = export.ArtifactLength,
        PublishedAt = export.PublishedAt,
        ExpiresAt = export.ExpiresAt,
        ErrorCode = export.ErrorCode,
    };
    private static Result<AuditExportStatus> Failure(string suffix) => Result.Failure<AuditExportStatus>(new Error("auditing.export." + suffix, "调查导出未被接受，请核对筛选或使用原请求身份重试。"));
}

/// <summary>显式启用持久调查导出。</summary>
public static class AuditExportServices
{
    /// <summary>注册导出命令；只在拥有 Auditing PostgreSQL 的宿主启用。</summary>
    /// <param name="services">当前宿主容器。</param>
    /// <param name="options">有限执行配置。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddAuditExportPersistence(this IServiceCollection services, AuditExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new();
        options.Validate();
        services.AddSingleton(options);
        services.AddScoped<ICommandHandler<AcceptAuditExport, AuditExportStatus>, AuditExportCommands>();
        services.AddScoped<IQueryHandler<GetAuditExport, AuditExportStatus>, AuditExportQueries>();
        services.AddScoped<IQueryHandler<ListAuditExports, IReadOnlyList<AuditExportStatus>>, AuditExportQueries>();
        services.AddScoped<ICommandHandler<CancelAuditExport, AuditExportStatus>, AuditExportQueries>();
        services.AddScoped<ICommandHandler<RetryAuditExport, AuditExportStatus>, AuditExportQueries>();
        services.AddScoped<ICommandHandler<ClaimAuditExport, AuditExportLease?>, AuditExportProcessing>();
        services.AddScoped<ICommandHandler<ProcessAuditExport, AuditExportStatus>, AuditExportProcessing>();
        services.AddScoped<IQueryHandler<GetAuditExportArtifact, AuditExportArtifact>, AuditExportArtifactQuery>();
        return services;
    }
}
