using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.Auditing.Infrastructure;

internal sealed class InMemoryOperationJournal : IOperationJournal
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OutboxEntry> _records = [];
    private readonly HashSet<(string Source, Guid OperationId, string Phase)> _phases = [];
    internal InMemoryOutboxStore Outbox { get; } = new();

    public Task<Result> AppendAsync(OperationObservedV1 observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        var valid = OperationObservationIngestion.Validate(observation);
        if (valid.IsFailure) { return Task.FromResult(valid); }
        var record = OutboxEntry.From(observation, new SystemTextJsonIntegrationEventSerializer());
        lock (_gate)
        {
            if (_records.TryGetValue(record.Id, out var existing))
            {
                return Task.FromResult(existing.EventName == record.EventName && existing.Payload == record.Payload
                    ? Result.Success() : Result.Failure(OperationJournalErrors.IdentityConflict));
            }
            if (!_phases.Add((observation.Source, observation.OperationId, observation.Phase)))
            {
                return Task.FromResult(Result.Failure(OperationJournalErrors.IdentityConflict));
            }
            _records.Add(record.Id, record);
            Outbox.Enqueue(record);
            return Task.FromResult(Result.Success());
        }
    }
}
