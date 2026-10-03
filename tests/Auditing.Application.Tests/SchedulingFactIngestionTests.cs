using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class SchedulingFactIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("created", false)]
    [InlineData("enabled", false)]
    [InlineData("disabled", false)]
    [InlineData("rule-changed", false)]
    [InlineData("deferred", false)]
    [InlineData("failure-cleared", false)]
    [InlineData("rescheduled", false)]
    [InlineData("advanced", false)]
    [InlineData("triggered", true)]
    [InlineData("coalesced", true)]
    [InlineData("skipped", true)]
    public async Task PlanFact_IsQueryable_AndDecisionAndExecutionAreImmutableOnRedelivery(string operation, bool hasDecision)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new SchedulingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message(operation, hasDecision ? Guid.NewGuid() : null);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { PlanId = 801 }, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { Execution = message.Execution! with { OperationId = Guid.NewGuid() } }, serializer).ToEnvelope()));
        var fact = Assert.Single((await store.QueryAsync(new AuditQuery(1, 10) { Source = "scheduling", SubjectType = "scheduled-task", SubjectId = "800" })).Entries).Fact;
        Assert.Equal("scheduling.plan." + operation, fact.Action);
        Assert.Equal(2, fact.SubjectVersion);
        Assert.Null(fact.ActorId);
        Assert.Equal(message.Execution!.OperationId, fact.Execution!.OperationId);
        if (hasDecision)
        {
            Assert.Equal(new AuditSubjectReference("scheduling", "schedule-decision", message.DecisionId!.Value.ToString("D")), fact.RelatedSubject);
            Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { DecisionId = Guid.NewGuid() }, serializer).ToEnvelope()));
            Assert.Single((await store.QueryAsync(new AuditQuery(1, 10)
            { RelatedContext = "scheduling", RelatedSubjectType = "schedule-decision", RelatedSubjectId = message.DecisionId.Value.ToString("D") })).Entries);
        }
        else { Assert.Null(fact.RelatedSubject); }
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Theory]
    [InlineData("completed", 800, 2, "none")]
    [InlineData("created", 0, 2, "none")]
    [InlineData("created", 800, 0, "none")]
    [InlineData("created", 800, 2, "valid")]
    [InlineData("deferred", 800, 2, "valid")]
    [InlineData("advanced", 800, 2, "valid")]
    [InlineData("triggered", 800, 2, "none")]
    [InlineData("coalesced", 800, 2, "none")]
    [InlineData("skipped", 800, 2, "none")]
    [InlineData("triggered", 800, 2, "empty")]
    public async Task InvalidFact_DoesNotConsumeMessageIdentity(string operation, long id, long version, string decision)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new SchedulingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var valid = Message("created", null);
        var invalid = valid with { Operation = operation, PlanId = id, Version = version, DecisionId = decision switch { "valid" => Guid.NewGuid(), "empty" => Guid.Empty, _ => null } };
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(invalid, serializer).ToEnvelope()));
        Assert.Empty((await store.QueryAsync(1, 10)).Entries);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(valid, serializer).ToEnvelope()));
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    private static PlanCommittedV1 Message(string operation, Guid? decisionId) => new()
    {
        PlanId = 800,
        Operation = operation,
        Version = 2,
        DecisionId = decisionId,
        OccurredAt = Now,
        TraceId = "schedule-trace",
        CorrelationId = "schedule-correlation",
        Execution = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "42", "schedule-trace", "schedule-correlation"),
    };
}
