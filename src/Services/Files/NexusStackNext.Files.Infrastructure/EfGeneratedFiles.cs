using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure.Persistence;

namespace NexusStackNext.Files.Infrastructure;

internal sealed partial class EfStoredFileRepository
{
    public Task<StoredFile?> FindOwnedCandidateAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id && file.OwnerId == ownerId
            && file.Candidate != null && file.Candidate.PublishedAt != null, cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> ReadExpiredCandidatesAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = await ReadNowAsync(cancellationToken).ConfigureAwait(false);
        return await context.Files.AsNoTracking().Where(file => !file.IsDeleted && file.Candidate != null
                && EF.Property<DateTimeOffset?>(file, "CandidateDeadline") <= now)
            .OrderBy(file => EF.Property<DateTimeOffset?>(file, "CandidateDeadline")).ThenBy(file => file.Id)
            .Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<StoredFile>> ExpireCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default) =>
        DecideCandidateAsync(producer, uploadId, (current, now) =>
        {
            if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
            current.ExpireCandidate(now);
            return Result.Success(current);
        }, cancellationToken);

    public Task<DateTimeOffset> ReadNowAsync(CancellationToken cancellationToken = default) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    public Task<StoredFile?> FindPublicationAsync(string producer, Guid publicationId, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Candidate != null
            && file.Candidate.Producer == producer && file.Candidate.PublicationId == publicationId, cancellationToken);

    public Task<Result<StoredFile>> PublishCandidateAsync(string producer, Guid uploadId, Guid publicationId, TimeSpan downloadLifetime,
        CancellationToken cancellationToken = default) => DecideCandidateAsync(producer, uploadId, (current, now) =>
    {
        if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
        var result = current.PublishCandidate(publicationId, now, now + downloadLifetime);
        return result.IsSuccess ? Result.Success(current) : Result.Failure<StoredFile>(result.Error);
    }, cancellationToken, publicationId);

    public Task<StoredFile?> FindCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default) =>
        context.Files.AsNoTracking().SingleOrDefaultAsync(file => file.Candidate != null
            && file.Candidate.Producer == producer && file.Candidate.UploadId == uploadId, cancellationToken);

    public Task<Result<StoredFile>> RegisterCandidateAsync(StoredFileId id, FileCandidate description, TimeSpan stageLifetime,
        CancellationToken cancellationToken = default) => DecideCandidateAsync(description.Producer, description.UploadId, (current, now) =>
    {
        if (current is not null)
        {
            return current.Candidate!.HasSameDescription(description) ? Result.Success(current)
                : Result.Failure<StoredFile>(new Error("files.candidate.conflict", "该上传身份已用于另一份描述。"));
        }
        return Result.Success(StoredFile.RegisterCandidate(id, description.WithDeadline(now + stageLifetime), now));
    }, cancellationToken);

    public Task<Result<StoredFile>> SealCandidateAsync(string producer, Guid uploadId, string storageKey, long length, string sha256,
        CancellationToken cancellationToken = default) => DecideCandidateAsync(producer, uploadId, (current, now) =>
    {
        if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
        var sealedFile = current.SealCandidate(storageKey, length, sha256, now);
        return sealedFile.IsSuccess ? Result.Success(current) : Result.Failure<StoredFile>(sealedFile.Error);
    }, cancellationToken);

    private async Task<Result<StoredFile>> DecideCandidateAsync(string producer, Guid uploadId,
        Func<StoredFile?, DateTimeOffset, Result<StoredFile>> decide, CancellationToken cancellationToken, Guid? publicationId = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                try
                {
                    await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '10s'", token).ConfigureAwait(false);
                    var identity = producer + "/" + uploadId.ToString("N");
                    await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 149149))", token).ConfigureAwait(false);
                    if (publicationId is { } publication)
                    {
                        var publicationIdentity = producer + "/" + publication.ToString("N");
                        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({publicationIdentity}, 149150))", token).ConfigureAwait(false);
                        var existing = await FindPublicationAsync(producer, publication, token).ConfigureAwait(false);
                        if (existing is not null && existing.Candidate!.UploadId != uploadId)
                        { return Result.Failure<StoredFile>(new Error("files.candidate.conflict", "该发布身份已用于另一候选。")); }
                    }
                    var before = await FindCandidateAsync(producer, uploadId, token).ConfigureAwait(false);
                    var now = await ReadNowAsync(token).ConfigureAwait(false);
                    var result = decide(before?.Snapshot(), now);
                    if (result.IsFailure || (before is not null && before.Version == result.Value.Version)) { return result; }
                    var file = result.Value;
                    if (file.StorageKey is not null)
                    {
                        await LockStorageKeyAsync(file.StorageKey, token).ConfigureAwait(false);
                        if (await context.RetiredStorageKeys.AnyAsync(key => key.StorageKey == file.StorageKey, token).ConfigureAwait(false))
                        { return Result.Failure<StoredFile>(new Error("files.candidate.closed", "该内容已退役。")); }
                    }
                    if (before is null) { context.Files.Add(file); }
                    else
                    {
                        var entry = context.Attach(file);
                        entry.State = EntityState.Modified;
                        entry.Property(item => item.Version).OriginalValue = before.Version;
                        entry.Reference(item => item.Candidate).TargetEntry!.State = EntityState.Modified;
                        entry.Property(FilesDbContext.DeletionOriginProperty).IsModified = false;
                    }
                    context.Outbox.AddRange(facts.Create(before, file));
                    await context.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return Result.Success(file);
                }
                catch (DbUpdateConcurrencyException) { throw new FileMetadataConflictException(); }
                catch (DbUpdateException error) when (CommittedFactCapacityFailure.Read(error, FilesDbContext.SchemaName, FileAuditCapacityException.Error) is { } reason)
                { throw new FileAuditCapacityException(reason); }
                finally { context.ChangeTracker.Clear(); }
            }, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result.Failure<StoredFile>(new Error("files.candidate.unavailable", "裁决预算已用尽；请按原身份查询或重试。")); }
    }
}
