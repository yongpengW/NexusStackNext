using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingFactAtomicityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MemoryFactFailure_RollsBackCreationMaintenanceAndDecision_AndSameStoreCanRetry()
    {
        var serializer = new RefusingFactSerializer();
        var store = new InMemoryScheduledTaskStore(serializer, new FixedClock(Now));
        await AssertAtomicityAsync<InvalidOperationException>(store, store, operation =>
        {
            serializer.RejectedOperation = operation;
            return Task.CompletedTask;
        });
    }

    [PostgresFact]
    public async Task PostgresFactFailure_RollsBackCreationMaintenanceAndDecision_AndSameScopeCanRetry()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        await using var scope = app.Services.CreateAsyncScope();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await AssertAtomicityAsync<DbUpdateException>(scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>(),
            scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey), async operation =>
            {
                // 只对新建的临时库注入来源事实故障；状态结论从公开存储端口读取。
                var sql = operation is null ? "ALTER TABLE scheduling.outbox DROP CONSTRAINT reject_plan_fact"
                    : operation is "created" or "disabled" or "triggered"
                        ? $"ALTER TABLE scheduling.outbox ADD CONSTRAINT reject_plan_fact CHECK (\"EventName\" <> 'scheduling.plan-committed.v1' OR \"Payload\"::jsonb ->> 'operation' <> '{operation}')"
                        : throw new InvalidOperationException("测试故障动作未列入白名单。");
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            });
    }

    private static async Task AssertAtomicityAsync<TException>(IScheduledTaskStore store, IOutboxStore outbox, Func<string?, Task> reject)
        where TException : Exception
    {
        var plan = ScheduledTask.Create(new ScheduledTaskId(78001), TaskCode.Create("atomic-plan").Value, TimeSpan.FromHours(1), Now,
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42").Value;
        await reject("created");
        await Assert.ThrowsAsync<TException>(() => store.AddAsync(plan));
        Assert.Null(await store.FindAsync(plan.Id));
        Assert.Empty(await outbox.ReadPendingAsync(100, Now));
        await reject(null);
        Assert.True((await store.AddAsync(plan)).IsSuccess);
        var created = Assert.Single(await outbox.ReadPendingAsync(100, Now));

        plan.Disable();
        await reject("disabled");
        await Assert.ThrowsAsync<TException>(() => store.SaveAsync(plan, 1));
        Assert.Equal(1, (await store.FindAsync(plan.Id))!.Version);
        Assert.True((await store.FindAsync(plan.Id))!.IsEnabled);
        Assert.Equal(created.Id, Assert.Single(await outbox.ReadPendingAsync(100, Now)).Id);
        await reject(null);
        Assert.True((await store.SaveAsync(plan, 1)).IsSuccess);
        plan.EnableAt(Now);
        Assert.True((await store.SaveAsync(plan, 2)).IsSuccess);
        var stale = plan.Snapshot();
        Assert.True(plan.Advance(Now, Now.AddHours(1), trigger: true).IsSuccess);
        var decisionId = Guid.NewGuid();
        var occurrence = new ScheduleOccurrence(decisionId, plan.Id.Value, plan.TriggerSequence, Now, Now,
            plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
        var decision = new ScheduleDecision(decisionId, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule,
            "Triggered", Now, Now, Now.AddHours(1), decisionId);
        await reject("triggered");
        await Assert.ThrowsAsync<TException>(() => store.RecordDecisionAsync(plan, 3, decision, occurrence));
        Assert.Equal(3, (await store.FindAsync(plan.Id))!.Version);
        Assert.Empty((await store.ReadDecisionsAsync(plan.Id.Value, 0, 100)).Items);
        Assert.Empty((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 100)).Items);
        Assert.Equal(3, (await outbox.ReadPendingAsync(100, Now)).Count);
        await reject(null);
        Assert.True((await store.RecordDecisionAsync(plan, 3, decision, occurrence)).IsSuccess);
        Assert.True((await store.RecordDecisionAsync(plan, 3, decision, occurrence)).IsSuccess);
        stale.Disable();
        Assert.True((await store.SaveAsync(stale, 3)).IsFailure);
        Assert.Equal(4, (await store.FindAsync(plan.Id))!.Version);
        Assert.Single((await store.ReadDecisionsAsync(plan.Id.Value, 0, 100)).Items);
        Assert.Single((await store.ReadOccurrencesAsync(plan.Id.Value, 0, 100)).Items);
        var pending = await outbox.ReadPendingAsync(100, Now);
        Assert.Single(pending, entry => entry.EventName == ScheduleTriggeredV1.Name);
        Assert.Equal(4, pending.Count(entry => entry.EventName == PlanCommittedV1.Name));
        await outbox.MarkDeadLetteredAsync(created.Id, "test-fact-delivery", Now, 0);
        Assert.True((await store.RetryOccurrenceAsync(created.Id, Now)).IsFailure);
    }

    private sealed class RefusingFactSerializer : IIntegrationEventSerializer
    {
        private readonly SystemTextJsonIntegrationEventSerializer _inner = new();
        public string? RejectedOperation { get; set; }
        public string Serialize(IntegrationEvent integrationEvent) => integrationEvent is PlanCommittedV1 fact && fact.Operation == RejectedOperation
            ? throw new InvalidOperationException("测试事实序列化故障。") : _inner.Serialize(integrationEvent);
        public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
    }
}
