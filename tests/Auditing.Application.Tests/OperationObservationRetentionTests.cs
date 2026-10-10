using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class OperationObservationRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retention_DeletesExpiredOperationsTogether_AndKeepsFreshAndLateObservations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddAuditingInMemoryStorage();
        await using var app = services.BuildServiceProvider();
        var store = app.GetRequiredService<IOperationObservationStore>();
        var old = Guid.NewGuid();
        var ongoing = Guid.NewGuid();
        var late = Guid.NewGuid();
        var cutoff = Now.AddDays(-1);
        foreach (var observation in new[]
        {
            Observation(old, "started", Now.AddDays(-3)), Observation(old, "finished", Now.AddDays(-2)),
            Observation(ongoing, "started", Now.AddDays(-3)), Observation(ongoing, "finished", Now),
            Observation(late, "finished", Now), Observation(Guid.NewGuid(), "finished", cutoff),
            Observation(Guid.NewGuid(), "started", cutoff.AddHours(-1)),
        })
        {
            Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(observation)).Value);
        }

        Assert.Equal(2, await store.DeleteExpiredAsync(cutoff, 1));
        Assert.Equal(5, (await app.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
        Assert.Equal(1, await store.DeleteExpiredAsync(cutoff, 1));
        Assert.Equal(0, await store.DeleteExpiredAsync(cutoff, 1));
        var remaining = (await store.QueryAsync(new OperationQuery(1, 10))).Operations;
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, item => item.OperationId == old);
        var retained = Assert.Single(remaining, item => item.OperationId == ongoing);
        Assert.NotNull(retained.StartedAt);
        Assert.NotNull(retained.FinishedAt);
        Assert.Contains(remaining, item => item.OperationId == late);
    }

    [Fact]
    public async Task Expiration_EndsObservationDeduplication_WhileCancellationKeepsRetainedEvidence()
    {
        var store = new InMemoryOperationObservationStore(new FixedClock(Now));
        var original = Observation(Guid.NewGuid(), "finished", Now.AddDays(-3));
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(original)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteExpiredAsync(Now.AddDays(-1), 10, cancelled.Token));
        Assert.Single((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        Assert.Equal(1, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
        var replay = OperationObservation.Record(original.Id, original.Data, Now).Value;
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(replay)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(replay)).Value);
        Assert.Equal(OperationObservationIngestion.MessageConflict, (await store.AcceptAsync(
            OperationObservation.Record(original.Id, original.Data with { DurationMs = 2 }, Now).Value)).Error);
        Assert.Equal(0, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
    }

    private static OperationObservation Observation(Guid operation, string phase, DateTimeOffset recordedAt) =>
        OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
            new OperationObservationData(new OperationId(operation), "platform", "http", phase,
                phase == "finished" ? "completed" : null, Now.AddDays(-3), "actor", "trace", "GET", "/api/example",
                phase == "finished" ? 200 : null, phase == "finished" ? 1 : null), recordedAt).Value;
}
