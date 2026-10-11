using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportExecution(PricingDbContext database, PricingExportOptions options) : ICommandHandler<ClaimPricingExport, PricingExportLease?>,
    ICommandHandler<RenewPricingExport, PricingExportLease>, ICommandHandler<SelectPricingExportPublication, PricingExportStatus>
{
    public Task<Result<PricingExportLease?>> HandleAsync(ClaimPricingExport command, CancellationToken cancellationToken = default) =>
        ExecuteWithAvailabilityAsync(token => ClaimAsync(command, token), Unavailable, cancellationToken);

    private async Task<Result<PricingExportLease?>> ClaimAsync(ClaimPricingExport command, CancellationToken cancellationToken)
    {
        for (var scanned = 0; scanned < 8; scanned++)
        {
            database.ChangeTracker.Clear();
            // 每轮只裁决一个聚合；即使预算耗尽后继续扫描，也先结束本轮事务。
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
                cancellationToken).ConfigureAwait(false);
            var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
            var export = await database.Exports.FromSqlInterpolated($"""
                    SELECT * FROM pricing.exports
                    WHERE ("State" = 'Queued' AND "AvailableAt" <= {now}) OR ("State" = 'Generating' AND "LeaseUntil" <= {now})
                    ORDER BY "AcceptedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (export is null) { return Result.Success<PricingExportLease?>(null); }
            var claimed = export.TryClaim(now, export.UploadId ?? Guid.NewGuid(), options.LeaseDuration, options.MaxExecutionDuration, options.MaxAttempts);
            if (claimed.IsFailure) { return Result.Failure<PricingExportLease?>(claimed.Error); }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (claimed.Value)
            {
                return Result.Success<PricingExportLease?>(new(export.Id.Value, export.Epoch, export.UploadId!.Value,
                    export.LeaseUntil!.Value, export.MaxLeaseUntil!.Value));
            }
        }
        return Result.Success<PricingExportLease?>(null);
    }

    private static Result<PricingExportLease?> Unavailable() => Result.Failure<PricingExportLease?>(
        new Error("pricing.export.unavailable", "导出暂时不可用，请稍后恢复。"));

    public Task<Result<PricingExportLease>> HandleAsync(RenewPricingExport command, CancellationToken cancellationToken = default) =>
        ExecuteWithAvailabilityAsync(token => RenewAsync(command, token), UnavailableLease, cancellationToken);

    private async Task<Result<PricingExportLease>> RenewAsync(RenewPricingExport command, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
            cancellationToken).ConfigureAwait(false);
        var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {command.ExportId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (export is null) { return Result.Failure<PricingExportLease>(new Error("pricing.export.lease_lost", "当前执行权已失效。")); }
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var renewed = export.Renew(command.Epoch, now, options.LeaseDuration);
        if (renewed.IsFailure) { return Result.Failure<PricingExportLease>(renewed.Error); }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await database.Entry(export).ReloadAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new PricingExportLease(export.Id.Value, export.Epoch, export.UploadId!.Value,
            export.LeaseUntil!.Value, export.MaxLeaseUntil!.Value));
    }

    public Task<Result<PricingExportStatus>> HandleAsync(SelectPricingExportPublication command, CancellationToken cancellationToken = default) =>
        ExecuteWithAvailabilityAsync(token => SelectAsync(command, token), UnavailableStatus, cancellationToken);

    private async Task<Result<PricingExportStatus>> SelectAsync(SelectPricingExportPublication command, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
            cancellationToken).ConfigureAwait(false);
        var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {command.ExportId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (export is null) { return Failure("pricing.export.lease_lost", "当前执行权已失效。"); }
        var receipt = command.Receipt;
        var description = receipt?.Description;
        if (receipt is null || receipt.FileId <= 0 || receipt.UploadId != export.UploadId || receipt.Producer != "pricing"
            || receipt.Stage != "Staged" || receipt.AcceptedAt == default || receipt.StageExpiresAt <= receipt.AcceptedAt
            || receipt.SealedAt is null || receipt.SealedAt < receipt.AcceptedAt || receipt.SealedAt >= receipt.StageExpiresAt
            || description is null || description.OwnerId != export.OwnerId || description.SourceExportId != export.Id.Value
            || description.Format != export.Format || description.FormatVersion != 1 || description.ColumnSetVersion != 1)
        { return Failure("pricing.export.invalid_receipt", "成果回执与原快照不一致。"); }
        var now = await database.DatabaseTimeAsync(cancellationToken).ConfigureAwait(false);
        var selected = export.SelectPublication(command.Epoch, now, receipt.FileId, command.PublicationId, receipt.Producer, description.Sha256, description.Length);
        if (selected.IsFailure) { return Result.Failure<PricingExportStatus>(selected.Error); }
        if (selected.Value)
        {
            database.ExportPublications.Add(new PricingExportPublication
            {
                PublicationId = command.PublicationId,
                ExportId = export.Id,
                UploadId = receipt.UploadId,
                FileId = receipt.FileId,
                Producer = receipt.Producer,
                SelectedAt = now,
                AvailableAt = now,
            });
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await database.Entry(export).ReloadAsync(cancellationToken).ConfigureAwait(false);
        var intent = await database.ExportPublications.AsNoTracking().SingleAsync(x => x.ExportId == export.Id, cancellationToken).ConfigureAwait(false);
        var status = PricingExportCommands.Status(export, intent);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(status);
    }

    private static Result<PricingExportStatus> Failure(string code, string message) => Result.Failure<PricingExportStatus>(new Error(code, message));

    private async Task<Result<T>> ExecuteWithAvailabilityAsync<T>(Func<CancellationToken, Task<Result<T>>> operation,
        Func<Result<T>> unavailable, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await operation(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return unavailable(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return unavailable(); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static Result<PricingExportLease> UnavailableLease() => Result.Failure<PricingExportLease>(
        new Error("pricing.export.unavailable", "导出暂时不可用，请稍后恢复。"));

    private static Result<PricingExportStatus> UnavailableStatus() => Failure("pricing.export.unavailable", "导出暂时不可用，请按原身份恢复。");
}
