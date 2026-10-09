using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Pricing.Application;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportExecution(PricingDbContext database, PricingExportOptions options) : ICommandHandler<ClaimPricingExport, PricingExportLease?>,
    ICommandHandler<RenewPricingExport, PricingExportLease>, ICommandHandler<SelectPricingExportPublication, PricingExportStatus>
{
    public async Task<Result<PricingExportLease?>> HandleAsync(ClaimPricingExport command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var scanned = 0; scanned < 8; scanned++)
            {
                database.ChangeTracker.Clear();
                // 每轮只裁决一个聚合；即使预算耗尽后继续扫描，也先结束本轮事务。
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
                await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
                    budget.Token).ConfigureAwait(false);
                var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
                var export = await database.Exports.FromSqlInterpolated($"""
                    SELECT * FROM pricing.exports
                    WHERE ("State" = 'Queued' AND "AvailableAt" <= {now}) OR ("State" = 'Generating' AND "LeaseUntil" <= {now})
                    ORDER BY "AcceptedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (export is null) { return Result.Success<PricingExportLease?>(null); }
                var claimed = export.TryClaim(now, export.UploadId ?? Guid.NewGuid(), options.LeaseDuration, options.MaxExecutionDuration, options.MaxAttempts);
                if (claimed.IsFailure) { return Result.Failure<PricingExportLease?>(claimed.Error); }
                await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
                await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                if (claimed.Value)
                {
                    return Result.Success<PricingExportLease?>(new(export.Id.Value, export.Epoch, export.UploadId!.Value,
                        export.LeaseUntil!.Value, export.MaxLeaseUntil!.Value));
                }
            }
            return Result.Success<PricingExportLease?>(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unavailable(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException)
        { return Unavailable(); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static Result<PricingExportLease?> Unavailable() => Result.Failure<PricingExportLease?>(
        new Error("pricing.export.unavailable", "导出暂时不可用，请稍后恢复。"));

    public async Task<Result<PricingExportLease>> HandleAsync(RenewPricingExport command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
                budget.Token).ConfigureAwait(false);
            var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {command.ExportId} FOR UPDATE")
                .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Result.Failure<PricingExportLease>(new Error("pricing.export.lease_lost", "当前执行权已失效。")); }
            var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
            var renewed = export.Renew(command.Epoch, now, options.LeaseDuration);
            if (renewed.IsFailure) { return Result.Failure<PricingExportLease>(renewed.Error); }
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(new PricingExportLease(export.Id.Value, export.Epoch, export.UploadId!.Value,
                export.LeaseUntil!.Value, export.MaxLeaseUntil!.Value));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result.Failure<PricingExportLease>(new Error("pricing.export.unavailable", "导出暂时不可用，请稍后恢复。")); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException)
        { return Result.Failure<PricingExportLease>(new Error("pricing.export.unavailable", "导出暂时不可用，请稍后恢复。")); }
        finally { database.ChangeTracker.Clear(); }
    }

    public async Task<Result<PricingExportStatus>> HandleAsync(SelectPricingExportPublication command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';",
                budget.Token).ConfigureAwait(false);
            var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {command.ExportId} FOR UPDATE")
                .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Failure("pricing.export.lease_lost", "当前执行权已失效。"); }
            var receipt = command.Receipt;
            var expected = new GeneratedFileDescriptionV1(export.OwnerId, export.Id.Value, export.SnapshotDigest, export.SnapshotLength, "csv", 1, 1);
            if (receipt is null || receipt.FileId <= 0 || receipt.UploadId != export.UploadId || receipt.Producer != "pricing"
                || receipt.Stage != "Staged" || receipt.AcceptedAt == default || receipt.StageExpiresAt <= receipt.AcceptedAt
                || receipt.SealedAt is null || receipt.SealedAt < receipt.AcceptedAt || receipt.SealedAt >= receipt.StageExpiresAt || receipt.Description != expected)
            { return Failure("pricing.export.invalid_receipt", "成果回执与原快照不一致。"); }
            var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
            var selected = export.SelectPublication(command.Epoch, now, receipt.FileId, command.PublicationId, receipt.Producer);
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
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
            var intent = await database.ExportPublications.AsNoTracking().SingleAsync(x => x.ExportId == export.Id, budget.Token).ConfigureAwait(false);
            var status = PricingExportCommands.Status(export, intent);
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("pricing.export.unavailable", "导出暂时不可用，请按原身份恢复。"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure("pricing.export.unavailable", "导出暂时不可用，请按原身份恢复。"); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static Result<PricingExportStatus> Failure(string code, string message) => Result.Failure<PricingExportStatus>(new Error(code, message));
}
