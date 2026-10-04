using System.Diagnostics;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

public sealed class CommittedFactMemoryCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneCleanup_UsesFiniteDefaultOrExplicitWriteBudget_ThenRecovers(bool explicitBudget)
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var entry = Entry(now) with { DeliveredAt = now.AddDays(-1) };
        var entries = new Dictionary<Guid, OutboxEntry> { [entry.Id] = entry };
        var gate = new Lock();
        var cleanup = new InMemoryCommittedFactCleanup(gate, () => entries, "fact.v1",
            new() { DeliveredRetention = TimeSpan.FromHours(1) }, new FixedClock(now),
            write: explicitBudget ? new() { Timeout = TimeSpan.FromMilliseconds(150) } : null);
        using var release = new ManualResetEventSlim();
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                locked.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        Task? waiting = null;
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var started = Stopwatch.GetTimestamp();
            waiting = Task.Run(() => cleanup.CleanupAsync());
            await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(6)));
            if (!explicitBudget) { Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 2.5, 5.5); }
            Assert.Equal(entry, Assert.Single(entries.Values));
        }
        finally
        {
            release.Set();
            await holder;
            if (waiting is not null)
            {
                try { await waiting; }
                catch (CommittedFactCapacityBusyException) { }
            }
        }
        Assert.Equal(1, await Task.Run(() => cleanup.CleanupAsync()).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(entries);
        Assert.Equal(0, await cleanup.CleanupAsync());
    }

    [Fact]
    public async Task StandaloneCleanup_PreservesUnknownCallbackFailure_AndReleasesGateForAnotherThread()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var entry = Entry(now) with { DeliveredAt = now.AddDays(-1) };
        var entries = new Dictionary<Guid, OutboxEntry> { [entry.Id] = entry };
        var error = new InvalidOperationException("Injected committed snapshot failure.");
        var reject = true;
        var cleanup = new InMemoryCommittedFactCleanup(new Lock(), () => reject ? throw error : entries, "fact.v1",
            new() { DeliveredRetention = TimeSpan.FromHours(1) }, new FixedClock(now),
            write: new() { Timeout = TimeSpan.FromMilliseconds(150) });
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.CleanupAsync()));
        Assert.Equal(entry, Assert.Single(entries.Values));
        reject = false;
        Assert.Equal(1, await Task.Run(() => cleanup.CleanupAsync()).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Cleanup_CanCancelWhileWaitingForWriter_WithoutDeletingAnything()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var entry = Entry(now) with { DeliveredAt = now.AddDays(-1) };
        var entries = new Dictionary<Guid, OutboxEntry> { [entry.Id] = entry };
        var gate = new Lock();
        var cleanup = new InMemoryCommittedFactCleanup(gate, () => entries, "fact.v1",
            new() { DeliveredRetention = TimeSpan.FromHours(1) }, new FixedClock(now));
        using var release = new ManualResetEventSlim();
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                locked.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var waiting = Task.Run(() => cleanup.CleanupAsync(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(entry, Assert.Single(entries.Values));
        }
        finally { release.Set(); await holder.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, await cleanup.CleanupAsync());
    }

    [Fact]
    public async Task Cleanup_UsesLatestCommittedSnapshot_AndPreservesOtherEvidence()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var gate = new Lock();
        var entries = new Dictionary<Guid, OutboxEntry>();
        var clock = new MutableClock(now.AddTicks(-1));
        var cleanup = new InMemoryCommittedFactCleanup(gate, () => entries, "fact.v1",
            new() { BatchSize = 1, DeliveredRetention = TimeSpan.FromHours(1) }, clock);
        var confirmed = Entry(now) with { DeliveredAt = now.AddHours(-1) };
        var second = confirmed with { Id = Guid.NewGuid() };
        var pending = Entry(now);
        var dead = Entry(now) with { DeadLetteredAt = now.AddDays(-1) };
        var business = confirmed with { Id = Guid.NewGuid(), EventName = "business.v1" };
        var recent = confirmed with { Id = Guid.NewGuid(), DeliveredAt = now.AddMinutes(-59) };
        // Identity 提交会替换整个字典；维护器不能永久捕获旧字典。
        entries = new[] { confirmed, second, pending, dead, business, recent }.ToDictionary(entry => entry.Id);
        Assert.Equal(0, await cleanup.CleanupAsync());
        clock.UtcNow = now;
        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => cleanup.CleanupAsync())));
        Assert.Equal(new[] { 0, 1, 1 }, counts.Order().ToArray());
        Assert.Equal(new[] { pending.Id, dead.Id, business.Id, recent.Id }.Order(), entries.Keys.Order());
    }

    private static OutboxEntry Entry(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        EventName = "fact.v1",
        Payload = "{}",
        OccurredAt = now.AddDays(-2),
    };
}
