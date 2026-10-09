using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportCommands(PricingDbContext database, ICurrentUser user, IExecutionContext execution) : ICommandHandler<AcceptPricingExport, PricingExportStatus>,
    IQueryHandler<GetPricingExport, PricingExportStatus>, ICommandHandler<CancelPricingExport, PricingExportStatus>
{
    internal const string OriginProperty = "ExecutionOrigin";
    public async Task<Result<PricingExportStatus>> HandleAsync(AcceptPricingExport command, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 200 || owner.Any(char.IsControl))
        { return Failure("pricing.export.unauthenticated", "导出需要当前有效身份。"); }
        if (command.RequestId == Guid.Empty || command.ItemIds is null || command.ItemIds.Count > 5000
            || command.ItemIds.Contains(Guid.Empty) || command.CalculationState is not ("Any" or "Pending" or "Stale" or "Current")
            || command.FormatVersion != 1 || command.ColumnSetVersion != 1 || command.Format is not ("csv" or "xlsx"))
        { return Failure("pricing.export.invalid", "导出请求或筛选无效。"); }
        var ids = command.ItemIds.Distinct().OrderBy(static id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var canonical = "pricing-export-request/v1\n" + command.Format + "/1/columns/1\n" + command.CalculationState + "\n"
            + string.Join(',', ids.Select(static id => id.ToString("D")));
        var requestDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '5s';",
                budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"pricing-export/" + owner + "/" + command.RequestId.ToString("D")}, 150150))",
                budget.Token).ConfigureAwait(false);
            var existing = await database.Exports.AsNoTracking().Where(x => x.OwnerId == owner && x.RequestId == command.RequestId)
                .Select(x => new { x.Id, x.CanonicalRequest }).SingleOrDefaultAsync(
                budget.Token).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.CanonicalRequest == canonical ? Result.Success((await PricingExportReadModel.ReadAsync(database, existing.Id.Value, owner, budget.Token).ConfigureAwait(false))!.Status())
                    : Failure("pricing.export.request_conflict", "请求标识已用于不同导出内容。");
            }

            // 一条 SELECT 使用同一 MVCC 观察；重放在它之前结束，绝不读取活表或 Redis。
            var observed = await database.Database.SqlQueryRaw<ExportQuoteSnapshot>("""
                SELECT "Id" AS "ItemId", "Version", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "CostingRevision", "BreakEvenPrice",
                       statement_timestamp() AS "FrozenAt"
                FROM pricing.quotes
                WHERE (cardinality(@ids) = 0 OR "Id" = ANY(@ids))
                  AND (@state = 'Any' OR (@state = 'Pending' AND "CalculatedRevision" = 0)
                       OR (@state = 'Stale' AND "CalculatedRevision" > 0 AND "CalculatedRevision" < "InputRevision")
                       OR (@state = 'Current' AND "CalculatedRevision" > 0 AND "CalculatedRevision" = "InputRevision"))
                ORDER BY "Id" LIMIT 5001
                """, new NpgsqlParameter("ids", ids), new NpgsqlParameter("state", command.CalculationState))
                .ToArrayAsync(budget.Token).ConfigureAwait(false);
            if (observed.Length == 0) { return Failure("pricing.export.empty", "筛选结果为空，未接受导出。"); }
            if (observed.Length > 5000) { return Failure("pricing.export.too_many_rows", "报价超过五千行，未接受导出。"); }
            var rows = observed.Select(static row => new PricingExportRow(new PriceId(row.ItemId), row.Version, row.Cost, row.FeeRate,
                row.InputRevision, row.CalculatedRevision, row.CostingRevision, row.BreakEvenPrice)).ToArray();
            using var csv = new MemoryStream();
            await PricingCsvV1.WriteAsync(rows, csv, cancellationToken: budget.Token).ConfigureAwait(false);
            var snapshotDigest = Convert.ToHexStringLower(SHA256.HashData(csv.GetBuffer().AsSpan(0, checked((int)csv.Length))));
            var acceptedAt = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
            var accepted = PricingExport.Accept(new PricingExportId(Guid.NewGuid()), owner, command.RequestId, canonical,
                requestDigest, snapshotDigest, csv.Length, acceptedAt, observed[0].FrozenAt, rows, command.Format);
            if (accepted.IsFailure) { return Result.Failure<PricingExportStatus>(accepted.Error); }
            database.Exports.Add(accepted.Value);
            var origin = execution.Capture();
            database.Entry(accepted.Value).Property<ExecutionOrigin?>(OriginProperty).CurrentValue = origin?.IsValid() == true ? origin : null;
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            // 返回持久化精度的审计时间，首次返回与重启重放逐字段一致。
            await database.Entry(accepted.Value).ReloadAsync(budget.Token).ConfigureAwait(false);
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(Status(accepted.Value));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unavailable(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException)
        { return Unavailable(); }
        finally { database.ChangeTracker.Clear(); }
    }

    public async Task<Result<PricingExportStatus>> HandleAsync(GetPricingExport query, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner) || query.ExportId == Guid.Empty) { return Missing(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var export = await PricingExportReadModel.ReadAsync(database, query.ExportId, owner, budget.Token).ConfigureAwait(false);
            return export is null ? Missing() : Result.Success(export.Status());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException) { return Unavailable(); }
    }

    internal static PricingExportStatus Status(PricingExport export, PricingExportPublication? publication = null) => new(export.Id.Value, export.RequestId, export.Version,
        export.State, export.AcceptedAt, export.FrozenAt, export.RowCount, export.RequestDigest, export.SnapshotDigest)
    {
        Audit = EntityAuditMetadata.From(export),
        Format = export.Format,
        ArtifactDigest = export.ArtifactDigest,
        ArtifactLength = export.ArtifactLength,
        Epoch = export.Epoch,
        Attempts = export.Attempts,
        LeaseUntil = export.LeaseUntil,
        MaxLeaseUntil = export.MaxLeaseUntil,
        ErrorCode = export.ErrorCode,
        FileId = export.FileId,
        PublicationId = export.PublicationId,
        PublishedAt = export.PublishedAt,
        ExpiresAt = export.ExpiresAt,
        RetryRevision = export.RetryRevision,
        RequestDigestVersion = export.RequestDigestVersion,
        SnapshotDigestVersion = export.SnapshotDigestVersion,
        SnapshotLength = export.SnapshotLength,
        Delivery = PricingExportReadModel.Delivery(publication),
    };
    private static Result<PricingExportStatus> Failure(string code, string message) => Result.Failure<PricingExportStatus>(new Error(code, message));
    private static Result<PricingExportStatus> Missing() => Failure("pricing.export.not_found", "导出不存在。");
    private static Result<PricingExportStatus> Unavailable() => Failure("pricing.export.unavailable", "导出暂时不可用，请保留原请求身份重试。");

    public async Task<Result<PricingExportStatus>> HandleAsync(CancelPricingExport command, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner) || command.ExportId == Guid.Empty) { return Missing(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
                budget.Token).ConfigureAwait(false);
            var export = await database.Exports.FromSqlInterpolated(
                $"SELECT * FROM pricing.exports WHERE \"Id\" = {command.ExportId} AND \"OwnerId\" = {owner} FOR UPDATE")
                .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Missing(); }
            var canceled = export.Cancel(command.ExpectedVersion);
            if (canceled.IsFailure) { return Result.Failure<PricingExportStatus>(canceled.Error); }
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(Status(export));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Unavailable(); }
        finally { database.ChangeTracker.Clear(); }
    }
}

internal sealed class ExportQuoteSnapshot
{
    public Guid ItemId { get; set; }
    public long Version { get; set; }
    public decimal Cost { get; set; }
    public decimal FeeRate { get; set; }
    public long InputRevision { get; set; }
    public long CalculatedRevision { get; set; }
    public long CostingRevision { get; set; }
    public decimal? BreakEvenPrice { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
}
