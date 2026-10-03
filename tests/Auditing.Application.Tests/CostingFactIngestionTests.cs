using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class CostingFactIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("created")]
    [InlineData("inputs-changed")]
    [InlineData("result-applied")]
    public async Task CostFact_IsQueryable_AndExecutionIsImmutableAcrossRedelivery(string operation)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new CostingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message(operation);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { ItemId = Guid.NewGuid() }, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { Execution = message.Execution! with { OperationId = Guid.NewGuid() } }, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { Execution = message.Execution! with { InitiatorId = "other" } }, serializer).ToEnvelope()));
        var fact = Assert.Single((await store.QueryAsync(new AuditQuery(1, 10)
        {
            Source = "costing",
            SubjectType = "cost-sheet",
            SubjectId = message.ItemId.ToString("D"),
            OperationId = message.Execution!.OperationId,
        })).Entries).Fact;
        Assert.Equal("costing.cost-sheet." + operation, fact.Action);
        Assert.Equal(2, fact.SubjectVersion);
        Assert.Null(fact.ActorId);
        Assert.Null(fact.RelatedSubject);
        Assert.Equal(message.Execution.OperationId, fact.Execution!.OperationId);
        Assert.Equal(message.Execution.RootOperationId, fact.Execution.RootOperationId);
        Assert.Equal("42", fact.Execution.InitiatorId);
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Theory]
    [InlineData("unknown-action")]
    [InlineData("empty-item")]
    [InlineData("zero-version")]
    [InlineData("negative-version")]
    [InlineData("empty-operation")]
    [InlineData("empty-root")]
    [InlineData("invalid-source")]
    [InlineData("trace-mismatch")]
    [InlineData("correlation-mismatch")]
    [InlineData("invalid-correlation")]
    [InlineData("wrong-event")]
    [InlineData("wrong-message-id")]
    [InlineData("empty-message-id")]
    [InlineData("invalid-json")]
    [InlineData("null-payload")]
    public async Task InvalidFact_DoesNotConsumeTheMessageIdentity(string failure)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new CostingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var valid = Message("created");
        var invalid = failure switch
        {
            "unknown-action" => valid with { Operation = "completed" },
            "empty-item" => valid with { ItemId = Guid.Empty },
            "zero-version" => valid with { Version = 0 },
            "negative-version" => valid with { Version = -1 },
            "empty-operation" => valid with { Execution = valid.Execution! with { OperationId = Guid.Empty } },
            "empty-root" => valid with { Execution = valid.Execution! with { RootOperationId = Guid.Empty } },
            "invalid-source" => valid with { Execution = valid.Execution! with { Source = "\n" } },
            "trace-mismatch" => valid with { TraceId = "another-trace" },
            "correlation-mismatch" => valid with { CorrelationId = "another-correlation" },
            "invalid-correlation" => valid with { Execution = valid.Execution! with { CorrelationId = "\n" } },
            _ => valid,
        };
        var envelope = OutboxEntry.From(invalid, serializer).ToEnvelope();
        envelope = failure switch
        {
            "wrong-event" => envelope with { EventName = CostCalculatedV1.Name },
            "wrong-message-id" => envelope with { MessageId = Guid.NewGuid() },
            "empty-message-id" => envelope with { MessageId = Guid.Empty },
            "invalid-json" => envelope with { Payload = "{" },
            "null-payload" => envelope with { Payload = "null" },
            _ => envelope,
        };
        Assert.False(await consumer.HandleAsync(envelope));
        Assert.Empty((await store.QueryAsync(1, 10)).Entries);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(valid, serializer).ToEnvelope()));
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Fact]
    public async Task UnknownExecution_RemainsUnknownOnRedelivery()
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new CostingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var known = Message("created");
        var unknown = known with { Execution = null };
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(unknown, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(known, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(unknown, serializer).ToEnvelope()));
        Assert.Null(Assert.Single((await store.QueryAsync(1, 10)).Entries).Fact.Execution);
    }

    private static CostSheetCommittedV1 Message(string operation) => new()
    {
        ItemId = Guid.NewGuid(),
        Operation = operation,
        Version = 2,
        OccurredAt = Now,
        TraceId = "cost-trace",
        CorrelationId = "cost-correlation",
        Execution = new ExecutionOrigin(Guid.NewGuid(), "costing", Guid.NewGuid(), "costing", "42", "cost-trace", "cost-correlation"),
    };
}
