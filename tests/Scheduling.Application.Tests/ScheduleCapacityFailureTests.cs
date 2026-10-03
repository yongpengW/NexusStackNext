using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Scheduling.Application.Tests;

public sealed class ScheduleCapacityFailureTests
{
    [Fact]
    public async Task CapacityError_WithDifferentDiagnosticText_IsStillReportedAsFailed()
    {
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var backing = new InMemoryScheduledTaskStore(new SystemTextJsonIntegrationEventSerializer(), clock);
        var plan = ScheduledTask.Create(new ScheduledTaskId(1), TaskCode.Create("capacity-rejection").Value,
            TimeSpan.FromHours(1), now, ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
        Assert.True((await backing.AddAsync(plan)).IsSuccess);
        var runner = new ScheduleRunner(new CapacityRejectingStore(backing), clock, new CronScheduleCalendar());

        var result = await runner.RunOnceAsync();

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Triggered);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(new long[] { 1 }, result.FailedPlanIds);
        var unchanged = Assert.IsType<ScheduledTask>(await backing.FindAsync(plan.Id));
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(now, unchanged.NextRunAt);
        Assert.Null(unchanged.RetryAt);
        Assert.Empty((await backing.ReadDecisionsAsync(1, 0, 10)).Items);
        Assert.Empty((await backing.ReadOccurrencesAsync(1, 0, 10)).Items);
        Assert.Single(await backing.ReadPendingAsync(10, now));
    }

    // 只替换公开存储端口的容量结论，其余读写由真实 Memory 适配器完成。
    private sealed class CapacityRejectingStore(IScheduledTaskStore backing) : IScheduledTaskStore
    {
        public Task<Result> RecordDecisionAsync(ScheduledTask task, long expectedVersion, ScheduleDecision decision,
            ScheduleOccurrence? occurrence, CancellationToken cancellationToken = default) => Task.FromResult(Result.Failure(
                new Error("scheduling.audit_capacity_exhausted", "Another storage adapter's capacity diagnostic.")));

        public Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
            => backing.ReadDueAsync(now, batchSize, cancellationToken);
        public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) => backing.ListAsync(cancellationToken);
        public Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default) => backing.FindAsync(id, cancellationToken);
        public Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default)
            => backing.ReadPageAsync(page, limit, cancellationToken);
        public Task<Result> AddAsync(ScheduledTask task, ExecutionOrigin? origin = null, CancellationToken cancellationToken = default)
            => backing.AddAsync(task, origin, cancellationToken);
        public Task<ExecutionOrigin?> ReadExecutionOriginAsync(ScheduledTaskId id, CancellationToken cancellationToken = default)
            => backing.ReadExecutionOriginAsync(id, cancellationToken);
        public Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default)
            => backing.SaveAsync(task, expectedVersion, cancellationToken);
        public Task<ScheduleDecisionPage> ReadDecisionsAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
            => backing.ReadDecisionsAsync(planId, offset, limit, cancellationToken);
        public Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default)
            => backing.ReadOccurrencesAsync(planId, offset, limit, cancellationToken);
        public Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt,
            CancellationToken cancellationToken = default) => backing.RetryOccurrenceAsync(occurrenceId, expectedDeadLetteredAt, cancellationToken);
    }
}
