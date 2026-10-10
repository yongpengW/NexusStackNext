using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Exports;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Contracts;
using Npgsql;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class AuditExportProcessing(AuditingDbContext database, AuditExportOptions options, IExportFiles files,
    IBackgroundExecutionObservation observations) : ICommandHandler<ClaimAuditExport, AuditExportLease?>, ICommandHandler<ProcessAuditExport, AuditExportStatus>
{
    public async Task<Result<AuditExportLease?>> HandleAsync(ClaimAuditExport command, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
                await ConfigureAsync(budget.Token).ConfigureAwait(false);
                if (!await database.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(680068) AS \"Value\"").SingleAsync(budget.Token).ConfigureAwait(false))
                { return Result.Success<AuditExportLease?>(null); }
                var now = await NowAsync(budget.Token).ConfigureAwait(false);
                if (await database.Exports.AnyAsync(x => (x.State == "Generating" || x.State == "Publishing") && x.LeaseUntil > now, budget.Token).ConfigureAwait(false))
                { return Result.Success<AuditExportLease?>(null); }
                var export = await database.Exports.FromSqlInterpolated($"""
                    SELECT * FROM auditing.exports
                    WHERE ("State" IN ('Queued', 'Generating', 'Publishing') AND "ReadyAt" <= {now}
                        AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {now}))
                        OR (jsonb_array_length("Rows") > 0 AND "AcceptedAt" + interval '7 days' <= {now})
                    ORDER BY "AcceptedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (export is null) { return Result.Success<AuditExportLease?>(null); }
                bool claimed;
                _ = export.ExpireSnapshot(now);
                claimed = export.TryClaim(now, options.LeaseDuration, options.MaxAttempts).Value;
                await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                return Result.Success<AuditExportLease?>(claimed ? new(export.Id.Value, export.Epoch, export.LeaseUntil!.Value) : null);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure<AuditExportLease?>("unavailable"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure<AuditExportLease?>("unavailable"); }
        finally { database.ChangeTracker.Clear(); }
    }

    public async Task<Result<AuditExportStatus>> HandleAsync(ProcessAuditExport command, CancellationToken cancellationToken = default)
    {
        var lease = command.Lease;
        Result<AuditExportStatus> result;
        try
        {
            result = await observations.ObserveAsync(new TaskExecutionDescriptor("auditing.export.process", lease.ExportId, lease.Epoch), async () =>
            {
                using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readBudget.CancelAfter(TimeSpan.FromSeconds(5));
                var id = new AuditExportId(lease.ExportId);
                var input = await database.Exports.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { Export = x, Origin = EF.Property<ExecutionOrigin?>(x, "ExecutionOrigin") }).SingleOrDefaultAsync(readBudget.Token).ConfigureAwait(false);
                return new BackgroundExecutionInput<AuditExport?>(input?.Export, input?.Origin);
            }, async export =>
            {
                if (export is null || export.Epoch != lease.Epoch || export.State is not ("Generating" or "Publishing")) { return Failure<AuditExportStatus>("lease_lost"); }
                using var clockBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                clockBudget.CancelAfter(TimeSpan.FromSeconds(5));
                var now = await NowAsync(clockBudget.Token).ConfigureAwait(false);
                if (export.LeaseUntil <= now) { return Failure<AuditExportStatus>("lease_lost"); }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(export.LeaseUntil!.Value - now);
                if (export.FileId is null)
                {
                    var found = await files.FindAsync(new ExportFileLookup(export.Id.Value, export.OwnerId, export.Id.Value, "xlsx"), budget.Token).ConfigureAwait(false);
                    if (found.IsFailure) { return Result.Failure<AuditExportStatus>(found.Error); }
                    GeneratedFileReceiptV1 staged;
                    if (found.Value is { Stage: "Staged" } original) { staged = original; }
                    else
                    {
                        var directory = options.TemporaryDirectory ?? Path.Combine(Path.GetTempPath(), "nsn-audit-exports");
                        Directory.CreateDirectory(directory);
                        var streamOptions = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.ReadWrite,
                            Share = FileShare.None,
                            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
                            BufferSize = 65_536,
                        };
                        if (!OperatingSystem.IsWindows()) { streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xlsx.tmp");
                        await using var content = new FileStream(path, streamOptions);
                        if (!OperatingSystem.IsWindows()) { File.Delete(path); }
                        await AuditXlsxV1.WriteAsync(export.Kind, export.Rows.Select(static row => row.ToArray()).ToArray(), content, options.MaxOutputBytes, budget.Token).ConfigureAwait(false);
                        content.Position = 0;
                        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, budget.Token).ConfigureAwait(false));
                        var description = new GeneratedFileDescriptionV1(export.OwnerId, export.Id.Value, digest, content.Length, "xlsx", 1, 1);
                        if (found.Value is { } pending && pending.Description != description) { return Failure<AuditExportStatus>("candidate_bytes_conflict"); }
                        var uploaded = await files.StageAsync(new ExportFileUpload(export.Id.Value, description), content, budget.Token).ConfigureAwait(false);
                        if (uploaded.IsFailure) { return Result.Failure<AuditExportStatus>(uploaded.Error); }
                        staged = uploaded.Value;
                    }
                    var selected = await MutateAsync(lease.ExportId, (current, at) => current.SelectPublication(lease.Epoch, at,
                        staged.FileId, staged.Description.Sha256, staged.Description.Length), budget.Token).ConfigureAwait(false);
                    if (selected.IsFailure) { return selected; }
                    // Only the committed selected identity may be published. Network I/O is outside the transaction.
                    var id = new AuditExportId(lease.ExportId);
                    export = await database.Exports.AsNoTracking().SingleAsync(x => x.Id == id, budget.Token).ConfigureAwait(false);
                }
                var expected = new GeneratedFileDescriptionV1(export.OwnerId, export.Id.Value, export.ArtifactDigest!, export.ArtifactLength!.Value, "xlsx", 1, 1);
                var published = await files.PublishAsync(new ExportFilePublication(export.Id.Value, export.FileId!.Value, export.Id.Value, expected), budget.Token).ConfigureAwait(false);
                return published.IsFailure ? Result.Failure<AuditExportStatus>(published.Error)
                    : await MutateAsync(lease.ExportId, (current, at) => current.Complete(lease.Epoch, at, published.Value.PublishedAt, published.Value.ExpiresAt), budget.Token).ConfigureAwait(false);
            }, static processed => processed.IsSuccess ? BackgroundExecutionOutcome.Completed : processed.Error.Code == "auditing.export.lease_lost"
                ? BackgroundExecutionOutcome.LeaseLost : BackgroundExecutionOutcome.Failed, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException) { result = Failure<AuditExportStatus>("output_limit"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result = Failure<AuditExportStatus>("storage_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { result = Failure<AuditExportStatus>("execution_timeout"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { result = Failure<AuditExportStatus>("unavailable"); }
        if (result.IsFailure && result.Error.Code != "auditing.export.lease_lost")
        {
            _ = await MutateAsync(lease.ExportId, (current, now) => current.Fail(lease.Epoch, now, result.Error.Code, options.MaxAttempts), cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<Result<AuditExportStatus>> MutateAsync(Guid id, Func<AuditExport, DateTimeOffset, Result> change, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                database.ChangeTracker.Clear();
                await using var transaction = await database.Database.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
                await ConfigureAsync(budget.Token).ConfigureAwait(false);
                var export = await database.Exports.FromSqlInterpolated($"SELECT * FROM auditing.exports WHERE \"Id\" = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
                if (export is null) { return Failure<AuditExportStatus>("lease_lost"); }
                var changed = change(export, await NowAsync(budget.Token).ConfigureAwait(false));
                if (changed.IsFailure) { return Result.Failure<AuditExportStatus>(changed.Error); }
                await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
                await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
                await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
                return Result.Success(AuditExportCommands.Status(export));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure<AuditExportStatus>("unavailable"); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Failure<AuditExportStatus>("unavailable"); }
        finally { database.ChangeTracker.Clear(); }
    }

    private Task<DateTimeOffset> NowAsync(CancellationToken token) => database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
    private Task<int> ConfigureAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '5s';", token);
    private static Result<T> Failure<T>(string code) => Result.Failure<T>(new Error("auditing.export." + code, "调查导出尚未完成，请查看原委托状态。"));
}
