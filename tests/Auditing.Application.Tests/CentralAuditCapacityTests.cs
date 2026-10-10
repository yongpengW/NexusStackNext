using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class CentralAuditCapacityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentMemoryWriters_CannotOverspend_AndCancelledAdmissionReservesNothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddAuditingInMemoryStorage(new() { MaxFacts = 1, MaxObservations = 1 });
        await using var app = services.BuildServiceProvider();
        await using (var cancelled = app.CreateAsyncScope())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ServiceProvider.GetRequiredService<AuditIngestion>()
                .IngestAsync(Fact(), cancellation.Token));
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var writes = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrivals) == 4) { allReady.SetResult(); }
            await ready.Task;
            await using var scope = app.CreateAsyncScope();
            try { return (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(Fact())).Value == IngestionOutcome.Accepted; }
            catch (AuditStorageUnavailableException error) when (error.CapacityExhausted) { return false; }
        })).ToArray();
        await allReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ready.SetResult();
        Assert.Single(await Task.WhenAll(writes), accepted => accepted);
        var capacity = await app.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync();
        Assert.Equal(new AuditStoragePoolCapacity(1, 1), capacity.Facts);
        Assert.Equal(new AuditStoragePoolCapacity(0, 1), capacity.Observations);
    }

    [Fact]
    public async Task FullFactPool_PreservesDuplicatesAndConflicts_AndRetainsNewMessageForRetry()
    {
        var clock = new FixedClock(Now);
        var store = new InMemoryAuditEntryStore(clock, new AuditStorageCapacityOptions { MaxFacts = 1 });
        var ingestion = new AuditIngestion(store, new SequentialIdGenerator(1000), clock);
        var original = Fact();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(original)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(original)).Value);
        Assert.Equal(AuditIngestion.MessageConflict, (await ingestion.IngestAsync(original with { SubjectId = "2" })).Error);
        var failure = await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => ingestion.IngestAsync(Fact()));
        Assert.True(failure.CapacityExhausted);
        Assert.Equal(original, Assert.Single((await store.QueryAsync(1, 10)).Entries).Fact);
    }

    private static AuditFact Fact() => new(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
        "global-setting", "1", 2, "actor", Now, "trace", "correlation");

    [Fact]
    public async Task FullObservationPool_PreservesPhaseIdentity_WithoutConsumingFactQuotaOrReservingRejectedPhases()
    {
        var clock = new FixedClock(Now);
        var options = new AuditStorageCapacityOptions { MaxFacts = 1, MaxObservations = 1 };
        var store = new InMemoryOperationObservationStore(clock, options);
        var original = Observation();
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(original)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
        var conflict = OperationObservation.Record(new OperationObservationId(Guid.NewGuid()), original.Data, Now).Value;
        Assert.Equal(OperationObservationIngestion.PhaseConflict, (await store.AcceptAsync(conflict)).Error);
        var rejected = Observation();
        Assert.True((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => store.AcceptAsync(rejected))).CapacityExhausted);
        Assert.True((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => store.AcceptAsync(rejected))).CapacityExhausted);
        Assert.Single((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        var facts = new InMemoryAuditEntryStore(clock, options);
        Assert.Equal(IngestionOutcome.Accepted, (await new AuditIngestion(facts, new SequentialIdGenerator(1000), clock).IngestAsync(Fact())).Value);
    }

    private static OperationObservation Observation() => OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
        new OperationObservationData(new OperationId(Guid.NewGuid()), "platform", "http", "finished", "completed", Now,
            "actor", "trace", "GET", "/api/example", 200, 1), Now).Value;
}
