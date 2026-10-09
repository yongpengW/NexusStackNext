using System.Data;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Domain;
using Npgsql;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportControl(PricingDbContext database, PricingExportOptions options, ICurrentUser user) : ICommandHandler<FailPricingExport, bool>, ICommandHandler<FailPricingExportPublication, bool>,
    ICommandHandler<RenewPricingExportPublication, PricingExportPublicationLease>, ICommandHandler<RetryPricingExport, PricingExportStatus>
{
    public async Task<Result<bool>> HandleAsync(FailPricingExport command, CancellationToken cancellationToken = default)
    {
        var code = SafeError(command.ErrorCode);
        var result = await ChangeAsync(command.ExportId, null, (export, _, now) => export.FailGeneration(command.Epoch, now, code, Permanent(code), options.MaxAttempts),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
    }

    public async Task<Result<bool>> HandleAsync(FailPricingExportPublication command, CancellationToken cancellationToken = default)
    {
        var code = SafeError(command.ErrorCode);
        var result = await ChangeAsync(command.Lease.ExportId, null, (export, intent, now) =>
        {
            if (intent is null || intent.PublicationId != command.Lease.PublicationId || !PricingExportPublicationExecution.Owns(intent, command.Lease.Epoch, now))
            { return Lost(); }
            intent.State = Permanent(code) || intent.Attempts >= options.MaxAttempts ? "Stopped" : "Pending";
            intent.AvailableAt = now.AddSeconds(Math.Min(30, 1 << Math.Min(intent.Attempts, 4)));
            intent.LeaseUntil = null;
            intent.StoppedAt = intent.State == "Stopped" ? now : null;
            intent.ErrorCode = code;
            return export.RecordPublicationProgress(code);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
    }

    public async Task<Result<PricingExportPublicationLease>> HandleAsync(RenewPricingExportPublication command, CancellationToken cancellationToken = default)
    {
        var result = await ChangeAsync(command.ExportId, null, (export, intent, now) =>
        {
            if (intent is null || intent.PublicationId != command.PublicationId || !PricingExportPublicationExecution.Owns(intent, command.Epoch, now)) { return Lost(); }
            var requested = now.Add(options.LeaseDuration);
            var extended = requested < intent.MaxLeaseUntil!.Value ? requested : intent.MaxLeaseUntil.Value;
            if (extended <= intent.LeaseUntil!.Value) { return Result.Success(); }
            intent.LeaseUntil = extended;
            return export.RecordPublicationProgress(intent.ErrorCode);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value.Lease is not null ? Result.Success(result.Value.Lease)
            : Result.Failure<PricingExportPublicationLease>(result.Error ?? new Error("pricing.export.lease_lost", "发布执行权已失效。"));
    }

    public async Task<Result<PricingExportStatus>> HandleAsync(RetryPricingExport command, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner)) { return Result.Failure<PricingExportStatus>(new Error("pricing.export.not_found", "导出不存在。")); }
        var result = await ChangeAsync(command.ExportId, owner, (export, intent, now) =>
        {
            if (export.State == "Failed") { return export.RetryGeneration(command.ExpectedVersion, now); }
            if (export.State != "Publishing" || export.Version != command.ExpectedVersion || intent is null || intent.State != "Stopped" || intent.RetryRevision >= 10)
            { return Result.Failure(new Error("pricing.export.retry_conflict", "当前停机状态不能按原观察恢复。")); }
            intent.State = "Pending";
            intent.RetryRevision++;
            intent.Attempts = 0;
            intent.AvailableAt = now;
            intent.StoppedAt = null;
            intent.LeaseUntil = null;
            intent.MaxLeaseUntil = null;
            intent.ErrorCode = null;
            return export.RecordPublicationProgress(null);
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success(result.Value.Status) : Result.Failure<PricingExportStatus>(result.Error);
    }

    private async Task<Result<ControlState>> ChangeAsync(Guid exportId, string? owner,
        Func<PricingExport, PricingExportPublication?, DateTimeOffset, Result> transition, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        database.ChangeTracker.Clear();
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, budget.Token).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'; SET LOCAL idle_in_transaction_session_timeout = '3s';", budget.Token).ConfigureAwait(false);
            var query = owner is null ? database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {exportId} FOR UPDATE")
                : database.Exports.FromSqlInterpolated($"SELECT * FROM pricing.exports WHERE \"Id\" = {exportId} AND \"OwnerId\" = {owner} FOR UPDATE");
            var export = await query.SingleOrDefaultAsync(budget.Token).ConfigureAwait(false);
            if (export is null) { return Result.Failure<ControlState>(new Error(owner is null ? "pricing.export.lease_lost" : "pricing.export.not_found", "工作不可用。")); }
            var intent = await database.ExportPublications.SingleOrDefaultAsync(x => x.ExportId == export.Id, budget.Token).ConfigureAwait(false);
            var now = await database.DatabaseTimeAsync(budget.Token).ConfigureAwait(false);
            var changed = transition(export, intent, now);
            if (changed.IsFailure) { return Result.Failure<ControlState>(changed.Error); }
            await database.SaveChangesAsync(budget.Token).ConfigureAwait(false);
            await database.Entry(export).ReloadAsync(budget.Token).ConfigureAwait(false);
            if (intent is not null) { await database.Entry(intent).ReloadAsync(budget.Token).ConfigureAwait(false); }
            var result = new ControlState(PricingExportCommands.Status(export, intent), intent?.LeaseUntil is null ? null : PricingExportPublicationExecution.Lease(intent));
            await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
            return Result.Success(result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Unavailable(); }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException) { return Unavailable(); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static string SafeError(string code) => code is "pricing.export.files_unavailable" or "pricing.export.unavailable" or "pricing.export.execution_timeout"
        or "pricing.export.storage_unavailable" or "pricing.export.generator_busy" or "pricing.export.file_closed" or "pricing.export.file_not_found"
        or "pricing.export.snapshot_corrupt" or "pricing.export.invalid_receipt" or "pricing.export.output_limit" ? code : "pricing.export.execution_failed";
    private static bool Permanent(string code) => code is "pricing.export.file_closed" or "pricing.export.file_not_found" or "pricing.export.snapshot_corrupt"
        or "pricing.export.invalid_receipt" or "pricing.export.output_limit";
    private static Result Lost() => Result.Failure(new Error("pricing.export.lease_lost", "当前发布执行权已失效。"));
    private static Result<ControlState> Unavailable() => Result.Failure<ControlState>(new Error("pricing.export.unavailable", "恢复暂不可用，请读取原工作状态。"));
    private sealed record ControlState(PricingExportStatus Status, PricingExportPublicationLease? Lease);
}
