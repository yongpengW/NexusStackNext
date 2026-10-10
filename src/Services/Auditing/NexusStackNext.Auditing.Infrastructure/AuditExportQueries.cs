using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Exports;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class AuditExportQueries(AuditingDbContext database, ICurrentUser user) : IQueryHandler<GetAuditExport, AuditExportStatus>,
    IQueryHandler<ListAuditExports, IReadOnlyList<AuditExportStatus>>, ICommandHandler<CancelAuditExport, AuditExportStatus>, ICommandHandler<RetryAuditExport, AuditExportStatus>
{
    internal static readonly Expression<Func<AuditExport, AuditExportStatus>> Projection = x => new(x.Id.Value, x.RequestId, x.Version, x.Kind,
        x.State, x.AcceptedAt, x.FrozenAt, x.From, x.To, x.RowCount, new EntityAuditMetadata(x.CreatedAt, x.CreatedBy, x.UpdatedAt, x.UpdatedBy))
    {
        Epoch = x.Epoch,
        Attempts = x.Attempts,
        LeaseUntil = x.LeaseUntil,
        FileId = x.FileId,
        ArtifactDigest = x.ArtifactDigest,
        ArtifactLength = x.ArtifactLength,
        PublishedAt = x.PublishedAt,
        ExpiresAt = x.ExpiresAt,
        ErrorCode = x.ErrorCode,
    };

    public async Task<Result<AuditExportStatus>> HandleAsync(GetAuditExport query, CancellationToken cancellationToken = default)
    {
        if (query.ExportId == Guid.Empty) { return Failure<AuditExportStatus>("invalid"); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var id = new AuditExportId(query.ExportId);
            var found = await database.Exports.AsNoTracking().Where(x => x.Id == id && x.OwnerId == user.UserId).Select(Projection)
                .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            return found is null ? Failure<AuditExportStatus>("not_found") : Result.Success(found);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure<AuditExportStatus>("unavailable"); }
        catch (NpgsqlException) { return Failure<AuditExportStatus>("unavailable"); }
    }

    public async Task<Result<IReadOnlyList<AuditExportStatus>>> HandleAsync(ListAuditExports query, CancellationToken cancellationToken = default)
    {
        if (query.Page is < 1 or > 1000 || query.Limit is < 1 or > 200) { return Failure<IReadOnlyList<AuditExportStatus>>("invalid"); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var found = await database.Exports.AsNoTracking().Where(x => x.OwnerId == user.UserId).OrderByDescending(x => x.AcceptedAt)
                .ThenByDescending(x => x.Id).Skip((query.Page - 1) * query.Limit).Take(query.Limit).Select(Projection).ToArrayAsync(budget.Token).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<AuditExportStatus>>(found);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure<IReadOnlyList<AuditExportStatus>>("unavailable"); }
        catch (NpgsqlException) { return Failure<IReadOnlyList<AuditExportStatus>>("unavailable"); }
    }

    public Task<Result<AuditExportStatus>> HandleAsync(CancelAuditExport command, CancellationToken cancellationToken = default) =>
        ControlAsync(command.ExportId, (export, _) => export.Cancel(command.ExpectedVersion), cancellationToken);

    public Task<Result<AuditExportStatus>> HandleAsync(RetryAuditExport command, CancellationToken cancellationToken = default) =>
        ControlAsync(command.ExportId, (export, now) => export.Retry(command.ExpectedVersion, now), cancellationToken);

    private async Task<Result<AuditExportStatus>> ControlAsync(Guid exportId, Func<AuditExport, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
                await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s';", budget.Token).ConfigureAwait(false);
                var found = await database.Exports.FromSqlInterpolated($"SELECT * FROM auditing.exports WHERE \"Id\" = {exportId} AND \"OwnerId\" = {user.UserId} FOR UPDATE")
                    .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (found is null) { return Failure<AuditExportStatus>("not_found"); }
                var now = await database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(budget.Token).ConfigureAwait(false);
                var cancelled = change(found, now);
                if (cancelled.IsFailure) { return Result.Failure<AuditExportStatus>(cancelled.Error); }
                await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                await database.Entry(found).ReloadAsync(budget.Token).ConfigureAwait(false);
                await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                return Result.Success(AuditExportCommands.Status(found));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure<AuditExportStatus>("unavailable"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure<AuditExportStatus>("unavailable"); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static Result<T> Failure<T>(string code) => Result.Failure<T>(new Error("auditing.export." + code, "导出不存在、参数无效或暂不可用。"));
}
