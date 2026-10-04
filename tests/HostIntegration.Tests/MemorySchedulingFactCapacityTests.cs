using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemorySchedulingFactCapacityTests
{
    [Fact]
    public async Task FullByteCapacity_PreservesReplayAndConflictSemantics_AndNeverCountsOrCleansOrdinaryMessages()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var serializer = new KnownPayloadSerializer { UseKnownPayload = value => value is PlanCommittedV1 };
        await using var baseApp = new CapacityApp(clock, maxPayloadBytes: 9, maxRecordPayloadBytes: 3) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var plan = CreatePlan(78031, clock.UtcNow);
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Advance(clock.UtcNow, clock.UtcNow.AddHours(1), trigger: true).IsSuccess);
        var id = Guid.NewGuid();
        var occurrence = new ScheduleOccurrence(id, plan.Id.Value, plan.TriggerSequence, clock.UtcNow, clock.UtcNow,
            plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
        var decision = new ScheduleDecision(id, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule,
            "Triggered", clock.UtcNow, clock.UtcNow, clock.UtcNow.AddHours(1), id);
        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.True((await store.AddAsync(CreatePlan(78032, clock.UtcNow.AddDays(1)))).IsSuccess);
        var original = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(3, original.Count(entry => entry.EventName == PlanCommittedV1.Name));
        var ordinary = Assert.Single(original, entry => entry.EventName == ScheduleTriggeredV1.Name);
        Assert.True(ordinary.Payload.Length > 9);

        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.True((await store.SaveAsync(plan, plan.Version)).IsSuccess);
        Assert.Equal(TaskRegistry.Conflict, (await store.SaveAsync(plan, 1)).Error);
        Assert.Equal(TaskRegistry.Conflict, (await store.RecordDecisionAsync(plan, 1, decision with { Kind = "Skipped" }, occurrence)).Error);
        var refused = CreatePlan(78033, clock.UtcNow.AddDays(1));
        var origin = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "original", "capacity-origin");
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(refused, origin)).Error);
        Assert.Null(await store.ReadExecutionOriginAsync(refused.Id));
        Assert.Null(await store.FindAsync(refused.Id));
        Assert.Equal(original, await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);

        foreach (var entry in original) { await outbox.MarkDeliveredAsync(entry.Id, clock.UtcNow); }
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(3, await cleanup.CleanupAsync());
        Assert.Equal("Delivered", Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items).DeliveryState);
        Assert.True((await store.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.Empty(await outbox.ReadPendingAsync(10, clock.UtcNow));
        var winner = origin with { OperationId = Guid.NewGuid(), InitiatorId = "winner" };
        Assert.True((await store.AddAsync(refused, winner)).IsSuccess);
        Assert.Equal(winner, await store.ReadExecutionOriginAsync(refused.Id));
        Assert.Single(await outbox.ReadPendingAsync(10, clock.UtcNow));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("decision")]
    public async Task CancellationDuringFactBatch_PublishesNothing_AndSameStoreRetainsItsFullBudget(string operation)
    {
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var serializer = new CancelingEventSerializer();
        var maxRecords = operation == "decision" ? 4 : operation == "update" ? 2 : 1;
        var store = new InMemoryScheduledTaskStore(serializer, new FixedClock(now), new() { MaxRecords = maxRecords });
        var plan = CreatePlan(78021, now);
        Func<CancellationToken, Task<Result>> commit;
        if (operation == "create") { commit = token => store.AddAsync(plan, cancellationToken: token); }
        else
        {
            Assert.True((await store.AddAsync(plan)).IsSuccess);
            if (operation == "update")
            {
                plan.Disable();
                commit = token => store.SaveAsync(plan, 1, token);
            }
            else
            {
                Assert.True(plan.Defer(now, "scheduling.commit.failed").IsSuccess);
                Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
                Assert.True(plan.Advance(now.AddMinutes(1), now.AddHours(1), trigger: true).IsSuccess);
                var id = Guid.NewGuid();
                var occurrence = new ScheduleOccurrence(id, plan.Id.Value, plan.TriggerSequence, now, now.AddMinutes(1),
                    plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
                var decision = new ScheduleDecision(id, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule,
                    "Triggered", now, now.AddMinutes(1), now.AddHours(1), id);
                commit = token => store.RecordDecisionAsync(plan, 2, decision, occurrence, token);
            }
        }
        var original = await store.FindAsync(plan.Id);
        var initial = await store.ReadPendingAsync(10, now);
        using var cancellation = new CancellationTokenSource();
        serializer.CancelOnSerialize = cancellation;
        serializer.ShouldCancel = value => value is PlanCommittedV1 fact && fact.Operation == (operation switch
        {
            "create" => "created",
            "update" => "disabled",
            _ => "triggered", // Cancel on the second fact, after failure-cleared was already constructed.
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit(cancellation.Token));
        var unchanged = await store.FindAsync(plan.Id);
        if (original is null) { Assert.Null(unchanged); }
        else
        {
            Assert.NotNull(unchanged);
            Assert.Equal((original.Version, original.IsEnabled, original.NextRunAt, original.RetryAt, original.TriggerSequence,
                original.SchedulingFailureCount, original.CreatedAt, original.UpdatedAt),
                (unchanged.Version, unchanged.IsEnabled, unchanged.NextRunAt, unchanged.RetryAt, unchanged.TriggerSequence,
                unchanged.SchedulingFailureCount, unchanged.CreatedAt, unchanged.UpdatedAt));
        }
        Assert.Equal(initial, await store.ReadPendingAsync(10, now));
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);

        serializer.CancelOnSerialize = null;
        Assert.True((await commit(CancellationToken.None)).IsSuccess);
        var pending = await store.ReadPendingAsync(10, now);
        Assert.Equal(maxRecords, pending.Count(entry => entry.EventName == PlanCommittedV1.Name));
        Assert.Equal(operation == "decision" ? 1 : 0, pending.Count(entry => entry.EventName == ScheduleTriggeredV1.Name));
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(CreatePlan(78022, now))).Error);
        Assert.Null(await store.FindAsync(new ScheduledTaskId(78022)));
    }

    [Fact]
    public async Task Utf8Limits_RejectSingleOversizeAndWholeRecoveryBatch_WithoutConsumingRemainingBytes()
    {
        var now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var serializer = new KnownPayloadSerializer { Payload = "中文" };
        var store = new InMemoryScheduledTaskStore(serializer, new FixedClock(now),
            new() { MaxRecords = 10, MaxPayloadBytes = 7, MaxRecordPayloadBytes = 3 });
        var plan = CreatePlan(78011, now);
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(plan)).Error);
        Assert.Null(await store.FindAsync(plan.Id));
        Assert.Empty(await store.ReadPendingAsync(10, now));

        // The two known Chinese characters each occupy exactly three UTF-8 bytes.
        serializer.Payload = "中";
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        serializer.Payload = "文";
        Assert.True(plan.Defer(now, "scheduling.commit.failed").IsSuccess);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        serializer.Payload = "a";
        var failed = await new ScheduleRunner(store, new FixedClock(now.AddMinutes(1)), new CronScheduleCalendar()).RunOnceAsync();
        Assert.Equal(new[] { plan.Id.Value }, failed.FailedPlanIds);
        Assert.Equal(0, failed.Triggered);
        Assert.Equal(0, failed.Skipped);
        Assert.Equal(2, (await store.FindAsync(plan.Id))!.Version);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(new[] { "中", "文" }, (await store.ReadPendingAsync(10, now)).Select(entry => entry.Payload).Order(StringComparer.Ordinal));
        Assert.True((await store.AddAsync(CreatePlan(78012, now))).IsSuccess);
        Assert.NotNull(await store.FindAsync(new ScheduledTaskId(78012)));
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(CreatePlan(78013, now))).Error);
        Assert.Null(await store.FindAsync(new ScheduledTaskId(78013)));
    }

    [Fact]
    public async Task RecoveryDecision_WhenOnlyOneOfTwoFactsFits_IsAtomic_AndOrdinaryMessagesDoNotUseFactCapacity()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var beginning = clock.UtcNow;
        await using var app = new CapacityApp(clock) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("scheduling");
        var plan = CreatePlan(78001, beginning);
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Defer(beginning, "scheduling.commit.failed").IsSuccess);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        var initial = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, initial.Count);
        Assert.All(initial, fact => Assert.Equal(PlanCommittedV1.Name, fact.EventName));
        clock.UtcNow = beginning.AddMinutes(1);
        var runner = new ScheduleRunner(store, clock, new CronScheduleCalendar());

        var rejected = await runner.RunOnceAsync();
        Assert.Equal(1, rejected.Examined);
        Assert.Equal(0, rejected.Triggered);
        Assert.Equal(0, rejected.Skipped);
        Assert.Equal(new[] { plan.Id.Value }, rejected.FailedPlanIds);
        var unchanged = await store.FindAsync(plan.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(2, unchanged.Version);
        Assert.Equal(beginning, unchanged.NextRunAt);
        Assert.Equal(plan.RetryAt, unchanged.RetryAt);
        Assert.Equal(1, unchanged.SchedulingFailureCount);
        Assert.Equal(0, unchanged.TriggerSequence);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal(initial, await outbox.ReadPendingAsync(10, clock.UtcNow));

        foreach (var fact in initial) { await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow); }
        Assert.Equal(0, await cleanup.CleanupAsync());
        Assert.Equal(new[] { plan.Id.Value }, (await runner.RunOnceAsync()).FailedPlanIds);
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        Assert.Equal(0, await cleanup.CleanupAsync());
        var recovered = await runner.RunOnceAsync();
        Assert.Equal(1, recovered.Triggered);
        Assert.Equal(0, recovered.Skipped);
        Assert.Empty(recovered.FailedPlanIds);
        var current = await store.FindAsync(plan.Id);
        Assert.NotNull(current);
        Assert.Equal(3, current.Version);
        Assert.Equal(1, current.TriggerSequence);
        Assert.Equal(0, current.SchedulingFailureCount);
        Assert.Null(current.RetryAt);
        var decision = Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
        var occurrence = Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
        Assert.Equal("Triggered", decision.Kind);
        Assert.Equal(decision.OccurrenceId, occurrence.OccurrenceId);
        var pending = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, pending.Count(fact => fact.EventName == PlanCommittedV1.Name));
        var ordinary = Assert.Single(pending, fact => fact.EventName == ScheduleTriggeredV1.Name);

        var lastSlot = CreatePlan(78002, clock.UtcNow.AddDays(1));
        Assert.True((await store.AddAsync(lastSlot)).IsSuccess);
        var excess = CreatePlan(78003, clock.UtcNow.AddDays(1));
        Assert.Equal(TaskRegistry.AuditCapacityExceeded, (await store.AddAsync(excess)).Error);
        Assert.Null(await store.FindAsync(excess.Id));
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        Assert.Equal(0, await cleanup.CleanupAsync());
        pending = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(3, pending.Count(fact => fact.EventName == PlanCommittedV1.Name));
        Assert.Equal(ordinary, Assert.Single(pending, fact => fact.EventName == ScheduleTriggeredV1.Name));
    }

    private static ScheduledTask CreatePlan(long id, DateTimeOffset firstRunAt) => ScheduledTask.Create(
        new ScheduledTaskId(id), TaskCode.Create($"memory-capacity-{id}").Value, TimeSpan.FromHours(1), firstRunAt,
        ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;

    private sealed class CapacityApp(MutableClock clock, long maxPayloadBytes = 268435456, int maxRecordPayloadBytes = 16384) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduling:AuditDelivery:MemoryCapacity:MaxRecords"] = "3",
                ["Scheduling:AuditDelivery:MemoryCapacity:MaxPayloadBytes"] = maxPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Scheduling:AuditDelivery:MemoryCapacity:MaxRecordPayloadBytes"] = maxRecordPayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Scheduling:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        }
    }
}
