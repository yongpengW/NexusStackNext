using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Infrastructure;

public sealed partial class InMemoryStoredFileRepository
{
    /// <inheritdoc />
    public Task<StoredFile?> FindOwnedCandidateAsync(StoredFileId id, string ownerId, CancellationToken cancellationToken = default)
    {
        using (_state.Capacity.Enter(cancellationToken))
        { return Task.FromResult(_state.Files.TryGetValue(id.Value, out var file) && file.OwnerId == ownerId && file.Candidate?.PublishedAt is not null ? file.Snapshot() : null); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredFile>> ReadExpiredCandidatesAsync(int limit, CancellationToken cancellationToken = default)
    {
        using (_state.Capacity.Enter(cancellationToken))
        {
            var now = _clock.UtcNow;
            return Task.FromResult<IReadOnlyList<StoredFile>>(_state.Files.Values.Where(file => !file.IsDeleted && file.Candidate != null
                && (file.Candidate.ExpiresAt ?? file.Candidate.StageExpiresAt) <= now)
                .OrderBy(file => file.Id.Value).Take(limit).Select(file => file.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<Result<StoredFile>> ExpireCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default) =>
        DecideCandidate(producer, uploadId, (current, now) =>
        {
            if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
            current.ExpireCandidate(now);
            return Result.Success(current);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<DateTimeOffset> ReadNowAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_clock.UtcNow); }

    /// <inheritdoc />
    public Task<StoredFile?> FindPublicationAsync(string producer, Guid publicationId, CancellationToken cancellationToken = default)
    {
        using (_state.Capacity.Enter(cancellationToken))
        { return Task.FromResult(_state.Files.Values.SingleOrDefault(file => file.Candidate?.Producer == producer && file.Candidate.PublicationId == publicationId)?.Snapshot()); }
    }

    /// <inheritdoc />
    public Task<Result<StoredFile>> PublishCandidateAsync(string producer, Guid uploadId, Guid publicationId, TimeSpan downloadLifetime,
        CancellationToken cancellationToken = default) => DecideCandidate(producer, uploadId, (current, now) =>
    {
        if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
        if (_state.Files.Values.Any(file => file.Candidate?.Producer == producer && file.Candidate.PublicationId == publicationId && file.Candidate.UploadId != uploadId))
        { return Result.Failure<StoredFile>(new Error("files.candidate.conflict", "该发布身份已用于另一候选。")); }
        var result = current.PublishCandidate(publicationId, now, now + downloadLifetime);
        return result.IsSuccess ? Result.Success(current) : Result.Failure<StoredFile>(result.Error);
    }, cancellationToken);

    /// <inheritdoc />
    public Task<StoredFile?> FindCandidateAsync(string producer, Guid uploadId, CancellationToken cancellationToken = default)
    {
        using (_state.Capacity.Enter(cancellationToken))
        { return Task.FromResult(_state.Files.Values.SingleOrDefault(file => file.Candidate?.Producer == producer && file.Candidate.UploadId == uploadId)?.Snapshot()); }
    }

    /// <inheritdoc />
    public Task<Result<StoredFile>> RegisterCandidateAsync(StoredFileId id, FileCandidate description, TimeSpan stageLifetime,
        CancellationToken cancellationToken = default) => DecideCandidate(producer: description.Producer, description.UploadId, (current, now) =>
    {
        if (current is not null)
        {
            return current.Candidate!.HasSameDescription(description) ? Result.Success(current)
            : Result.Failure<StoredFile>(new Error("files.candidate.conflict", "该上传身份已用于另一份描述。"));
        }
        return Result.Success(StoredFile.RegisterCandidate(id, description.WithDeadline(now + stageLifetime), now));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<Result<StoredFile>> SealCandidateAsync(string producer, Guid uploadId, string storageKey, long length, string sha256,
        CancellationToken cancellationToken = default) => DecideCandidate(producer, uploadId, (current, now) =>
    {
        if (current is null) { return Result.Failure<StoredFile>(new Error("files.not_found", "候选不存在。")); }
        var result = current.SealCandidate(storageKey, length, sha256, now);
        return result.IsSuccess ? Result.Success(current) : Result.Failure<StoredFile>(result.Error);
    }, cancellationToken);

    private Task<Result<StoredFile>> DecideCandidate(string producer, Guid uploadId,
        Func<StoredFile?, DateTimeOffset, Result<StoredFile>> decide, CancellationToken cancellationToken)
    {
        if (!_state.Capacity.TryEnter(out var scope, cancellationToken)) { throw new FileAuditCapacityException(); }
        using (scope)
        {
            var before = _state.Files.Values.SingleOrDefault(file => file.Candidate?.Producer == producer && file.Candidate.UploadId == uploadId);
            var result = decide(before?.Snapshot(), _clock.UtcNow);
            if (result.IsFailure || (before is not null && before.Version == result.Value.Version)) { return Task.FromResult(result); }
            var file = result.Value;
            if (file.StorageKey is not null && _state.Retired.Contains(file.StorageKey))
            { return Task.FromResult(Result.Failure<StoredFile>(new Error("files.candidate.closed", "该内容已退役。"))); }
            var facts = _facts.Create(before, file);
            var snapshot = file.Snapshot();
            Stamp(snapshot, before);
            if (!_state.Capacity.TryCommit(facts, () =>
            {
                foreach (var fact in facts) { _state.Outbox.Add(fact.Id, fact); }
                _state.Files[file.Id.Value] = snapshot;
                CopyAudit(snapshot, file);
            }, cancellationToken)) { throw new FileAuditCapacityException(); }
            return Task.FromResult(Result.Success(file));
        }
    }
}
