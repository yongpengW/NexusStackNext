using System.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Infrastructure;

// 文件仓储使用独立快照；比较本上下文实际保存前后的状态，不依赖 Attach 后的 IsModified 标记。
internal sealed class StoredFileCommittedFacts(IClock clock, IIntegrationEventSerializer serializer,
    ICurrentUser? currentUser = null, IExecutionContext? execution = null)
{
    public IReadOnlyList<OutboxEntry> Create(StoredFile? before, StoredFile after)
    {
        if (before is not null && before.Version == after.Version) { return []; }
        var operations = new List<string>();
        if (before is null) { operations.Add("registered"); }
        if (after.IsStored && (before is null || before.StorageKey != after.StorageKey || before.Size != after.Size)) { operations.Add("stored"); }
        if (after.Candidate?.PublishedAt is not null && before?.Candidate?.PublishedAt is null) { operations.Add("published"); }
        if (after.Candidate?.ExpiredAt is not null && before?.Candidate?.ExpiredAt is null) { operations.Add("expired"); }
        if (after.IsDeleted && before?.IsDeleted != true) { operations.Add("deletion-requested"); }
        if (after.NextCleanupAttemptAt is not null && before?.NextCleanupAttemptAt != after.NextCleanupAttemptAt) { operations.Add("cleanup-deferred"); }
        if (after.BytesRemovedAt is not null && before?.BytesRemovedAt != after.BytesRemovedAt) { operations.Add("bytes-removed"); }
        var origin = execution?.Capture();
        var trace = origin?.TraceId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var at = clock.UtcNow;
        return operations.Select(operation => OutboxEntry.From(new StoredFileCommittedV1
        {
            FileId = after.Id.Value,
            Operation = operation,
            Version = after.Version,
            ActorId = execution?.IsSystem == true ? null : currentUser?.UserId,
            OccurredAt = at,
            TraceId = trace,
            CorrelationId = origin?.CorrelationId ?? trace,
            Execution = origin,
        }, serializer)).ToArray();
    }
}
