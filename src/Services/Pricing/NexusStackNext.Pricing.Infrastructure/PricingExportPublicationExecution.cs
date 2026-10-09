using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Pricing.Application;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportPublicationExecution(PricingDbContext database, PricingExportOptions options) : ICommandHandler<ClaimPricingExportPublication, PricingExportPublicationLease?>,
    ICommandHandler<CompletePricingExportPublication, PricingExportStatus>
{
    public async Task<Result<PricingExportPublicationLease?>> HandleAsync(ClaimPricingExportPublication command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var scan = 0; scan < 8; scan++)
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
                await SetLimitsAsync(budget.Token).ConfigureAwait(false);
                var export = await database.Exports.FromSqlRaw("""
                    SELECT e.* FROM pricing.exports e WHERE e."State" = 'Publishing' AND EXISTS
                    (SELECT 1 FROM pricing.export_publications p WHERE p."ExportId" = e."Id"
                     AND ((p."State" = 'Pending' AND p."AvailableAt" <= clock_timestamp())
                       OR (p."State" = 'Delivering' AND p."LeaseUntil" <= clock_timestamp())))
                    ORDER BY e."AcceptedAt", e."Id" LIMIT 1 FOR UPDATE OF e SKIP LOCKED
                    """).SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (export is null) { return Result.Success<PricingExportPublicationLease?>(null); }
                // 所有发布修改先锁所属 Export；子 Outbox 与它是同一个聚合事务。
                var intent = await database.ExportPublications.SingleAsync(x => x.ExportId == export.Id, budget.Token).ConfigureAwait(false);
                var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
                if (intent.Attempts >= options.MaxAttempts)
                {
                    intent.State = "Stopped";
                    intent.StoppedAt = now;
                    intent.LeaseUntil = null;
                    intent.ErrorCode ??= "pricing.export.publication_exhausted";
                    _ = export.RecordPublicationProgress(intent.ErrorCode);
                    await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                    await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                    continue;
                }
                intent.State = "Delivering";
                intent.Epoch++;
                intent.Attempts++;
                intent.LeaseUntil = now.Add(options.LeaseDuration);
                intent.MaxLeaseUntil = now.Add(options.MaxExecutionDuration);
                _ = export.RecordPublicationProgress(intent.ErrorCode);
                await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                await database.Entry(intent).ReloadAsync(budget.Token).ConfigureAwait(false);
                await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                return Result.Success<PricingExportPublicationLease?>(Lease(intent));
            }
            return Result.Success<PricingExportPublicationLease?>(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable<PricingExportPublicationLease?>(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Unavailable<PricingExportPublicationLease?>(); }
        finally { database.ChangeTracker.Clear(); }
    }

    public async Task<Result<PricingExportStatus>> HandleAsync(CompletePricingExportPublication command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await SetLimitsAsync(budget.Token).ConfigureAwait(false);
            var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {command.Lease.ExportId} FOR UPDATE")
                .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Lost<PricingExportStatus>(); }
            var intent = await database.ExportPublications.SingleOrDefaultAsync(x => x.ExportId == export.Id, budget.Token).ConfigureAwait(false);
            if (intent is null || intent.PublicationId != command.Lease.PublicationId) { return Lost<PricingExportStatus>(); }
            var receipt = command.Receipt;
            var expected = new GeneratedFileDescriptionV1(export.OwnerId, export.Id.Value, export.SnapshotDigest, export.SnapshotLength, "csv", 1, 1);
            if (receipt is null || receipt.Producer != intent.Producer || receipt.FileId != intent.FileId || receipt.UploadId != intent.UploadId
                || receipt.PublicationId != intent.PublicationId || receipt.Description != expected || receipt.PublishedAt == default || receipt.ExpiresAt <= receipt.PublishedAt)
            { return Result.Failure<PricingExportStatus>(new Error("pricing.export.invalid_receipt", "成果回执与原发布意图不一致。")); }
            if (intent.State != "Delivered")
            {
                var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
                if (!Owns(intent, command.Lease.Epoch, now)) { return Lost<PricingExportStatus>(); }
                intent.State = "Delivered";
                intent.CompletedAt = now;
                intent.LeaseUntil = null;
                intent.ErrorCode = null;
            }
            var completed = export.CompletePublication(receipt.PublishedAt, receipt.ExpiresAt);
            if (completed.IsFailure) { return Result.Failure<PricingExportStatus>(completed.Error); }
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(PricingExportCommands.Status(export, intent));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable<PricingExportStatus>(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Unavailable<PricingExportStatus>(); }
        finally { database.ChangeTracker.Clear(); }
    }

    private Task<int> SetLimitsAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';", token);
    internal static PricingExportPublicationLease Lease(PricingExportPublication intent) => new(intent.ExportId.Value, intent.PublicationId,
        intent.Epoch, intent.LeaseUntil!.Value, intent.MaxLeaseUntil!.Value);
    internal static bool Owns(PricingExportPublication intent, long epoch, DateTimeOffset now) => intent.State == "Delivering" && intent.Epoch == epoch
        && intent.LeaseUntil is not null && intent.LeaseUntil > now && intent.MaxLeaseUntil is not null && intent.MaxLeaseUntil > now;
    private static Result<T> Lost<T>() => Result.Failure<T>(new Error("pricing.export.lease_lost", "当前发布执行权已失效。"));
    private static Result<T> Unavailable<T>() => Result.Failure<T>(new Error("pricing.export.unavailable", "导出暂不可用，请恢复原发布意图。"));
}
