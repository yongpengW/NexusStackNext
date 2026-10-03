using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.BuildingBlocks.Infrastructure.IntegrationTests;

public sealed class CommittedFactCleanupTests
{
    [PostgresFact]
    public async Task Cleanup_SkipsLockedCopies_AndRollsBackFailureOrCancellationBeforeConcurrentRecovery()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var first = Entry("probe.fact.v1", now.AddDays(-2)) with { DeliveredAt = now.AddDays(-1) };
        var second = first with { Id = Guid.NewGuid() };
        context.Outbox.AddRange(first, second);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var clock = new FixedClock(now);
        var cleanup = new EfCommittedFactCleanup<ProbeDbContext>(context, "probe.fact.v1", new() { DeliveredRetention = TimeSpan.FromHours(1) }, clock);
        await using var blocker = await database.OpenAsync();
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using var hold = new NpgsqlCommand("SELECT 1 FROM outbox WHERE \"Id\" = @id FOR UPDATE", blocker, transaction);
            hold.Parameters.AddWithValue("id", first.Id);
            await hold.ExecuteNonQueryAsync();
            Assert.Equal(1, await cleanup.CleanupAsync());
            Assert.Equal(first.Id, (await context.Outbox.AsNoTracking().SingleAsync()).Id);
            await transaction.RollbackAsync();
        }
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using (var hold = new NpgsqlCommand("LOCK TABLE outbox IN ACCESS EXCLUSIVE MODE", blocker, transaction)) { await hold.ExecuteNonQueryAsync(); }
            using var cancellation = new CancellationTokenSource();
            var waiting = cleanup.CleanupAsync(cancellation.Token);
            try
            {
                await Task.Delay(100);
                Assert.False(waiting.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            finally { cancellation.Cancel(); await transaction.RollbackAsync(); }
        }
        Assert.Equal(first.Id, (await context.Outbox.AsNoTracking().SingleAsync()).Id);
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION reject_fact_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private failure marker'; END $$;
            CREATE TRIGGER reject_fact_cleanup AFTER DELETE ON outbox FOR EACH STATEMENT EXECUTE FUNCTION reject_fact_cleanup();
            """, blocker)) { await inject.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<PostgresException>(() => cleanup.CleanupAsync());
        Assert.Equal(first.Id, (await context.Outbox.AsNoTracking().SingleAsync()).Id);
        await using (var release = new NpgsqlCommand("DROP TRIGGER reject_fact_cleanup ON outbox", blocker)) { await release.ExecuteNonQueryAsync(); }
        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var concurrent = await ProbeDatabase.NewContextAsync(database);
            return await new EfCommittedFactCleanup<ProbeDbContext>(concurrent, "probe.fact.v1", new() { DeliveredRetention = TimeSpan.FromHours(1) }, clock).CleanupAsync();
        }));
        Assert.Equal(new[] { 0, 0, 1 }, counts.Order().ToArray());
        Assert.Empty(await context.Outbox.AsNoTracking().ToArrayAsync());
    }

    [PostgresFact]
    public async Task Cleanup_DeletesOnlyConfirmedExpiredFacts_InBoundedBatches()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = await ProbeDatabase.CreateAsync(database);
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var old = now.AddHours(-2);
        var expired = Entry("probe.fact.v1", old) with { DeliveredAt = now.AddHours(-1) };
        var expiredSecond = Entry("probe.fact.v1", old) with { DeliveredAt = now.AddHours(-1) };
        var recent = Entry("probe.fact.v1", old) with { DeliveredAt = now.AddMinutes(-59) };
        var pending = Entry("probe.fact.v1", old);
        var stopped = Entry("probe.fact.v1", old) with { DeadLetteredAt = old };
        var business = Entry("probe.business.v1", old) with { DeliveredAt = old };
        context.Outbox.AddRange(expired, expiredSecond, recent, pending, stopped, business);
        await context.SaveChangesAsync();
        Assert.True(await new EfInboxStore<ProbeDbContext>(context).TryBeginProcessingAsync("central-audit", "probe.fact.v1", expired.Id, old));
        context.ChangeTracker.Clear();
        var cleanup = new EfCommittedFactCleanup<ProbeDbContext>(context, "probe.fact.v1",
            new() { DeliveredRetention = TimeSpan.FromHours(1), BatchSize = 1 }, clock);

        clock.UtcNow = now.AddTicks(-1);
        Assert.Equal(0, await cleanup.CleanupAsync());
        clock.UtcNow = now;
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(1, await cleanup.CleanupAsync());
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(new[] { recent.Id, pending.Id, stopped.Id, business.Id }.Order(),
            (await context.Outbox.AsNoTracking().Select(entry => entry.Id).ToArrayAsync()).Order());
        Assert.Single(await context.Inbox.AsNoTracking().ToArrayAsync());
    }

    private static OutboxEntry Entry(string eventName, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        EventName = eventName,
        Payload = "{}",
        OccurredAt = at,
    };
}
