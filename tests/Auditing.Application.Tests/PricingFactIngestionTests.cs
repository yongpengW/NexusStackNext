using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class PricingFactIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("created")]
    [InlineData("inputs-changed")]
    [InlineData("costing-applied")]
    [InlineData("result-applied")]
    public async Task PriceFact_PreservesItsSubjectCostReferenceAndExecutionOnRedelivery(string operation)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new PricingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message(operation);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { ItemId = Guid.NewGuid() }, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { Execution = message.Execution! with { OperationId = Guid.NewGuid() } }, serializer).ToEnvelope()));
        var fact = Assert.Single((await store.QueryAsync(new AuditQuery(1, 10)
        { Source = "pricing", SubjectType = "price-quote", SubjectId = message.ItemId.ToString("D") })).Entries).Fact;
        Assert.Equal("pricing.price-quote." + operation, fact.Action);
        Assert.Equal(2, fact.SubjectVersion);
        Assert.Null(fact.ActorId);
        Assert.Equal(message.Execution!.OperationId, fact.Execution!.OperationId);
        if (message.CostingItemId is { } related)
        {
            Assert.Equal(new AuditSubjectReference("costing", "cost-sheet", related.ToString("D")), fact.RelatedSubject);
            Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { CostingItemId = Guid.NewGuid() }, serializer).ToEnvelope()));
            Assert.Single((await store.QueryAsync(new AuditQuery(1, 10)
            { RelatedContext = "costing", RelatedSubjectType = "cost-sheet", RelatedSubjectId = related.ToString("D") })).Entries);
        }
        else { Assert.Null(fact.RelatedSubject); }
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("item")]
    [InlineData("version")]
    [InlineData("missing-cost")]
    [InlineData("empty-cost")]
    [InlineData("unrelated-cost")]
    [InlineData("trace")]
    [InlineData("correlation")]
    [InlineData("root")]
    [InlineData("identity")]
    [InlineData("event")]
    [InlineData("json")]
    public async Task InvalidFact_DoesNotReserveItsIdentity(string invalidField)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new PricingAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var valid = Message("costing-applied");
        var invalid = invalidField switch
        {
            "action" => valid with { Operation = "completed" },
            "item" => valid with { ItemId = Guid.Empty },
            "version" => valid with { Version = 0 },
            "missing-cost" => valid with { CostingItemId = null },
            "empty-cost" => valid with { CostingItemId = Guid.Empty },
            "unrelated-cost" => valid with { Operation = "result-applied" },
            "trace" => valid with { TraceId = "other-trace" },
            "correlation" => valid with { CorrelationId = "other-correlation" },
            "root" => valid with { Execution = valid.Execution! with { RootOperationId = Guid.Empty } },
            _ => valid,
        };
        var envelope = OutboxEntry.From(invalid, serializer).ToEnvelope();
        envelope = invalidField switch
        {
            "identity" => envelope with { MessageId = Guid.NewGuid() },
            "event" => envelope with { EventName = "pricing.unknown.v1" },
            "json" => envelope with { Payload = "null" },
            _ => envelope,
        };
        Assert.False(await consumer.HandleAsync(envelope));
        Assert.Empty((await store.QueryAsync(1, 10)).Entries);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(valid, serializer).ToEnvelope()));
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    private static PriceQuoteCommittedV1 Message(string operation) => new()
    {
        ItemId = Guid.NewGuid(),
        Operation = operation,
        Version = 2,
        CostingItemId = operation == "costing-applied" ? Guid.NewGuid() : null,
        OccurredAt = Now,
        TraceId = "price-trace",
        CorrelationId = "price-correlation",
        Execution = new ExecutionOrigin(Guid.NewGuid(), "pricing", Guid.NewGuid(), "costing", "42", "price-trace", "price-correlation"),
    };
}
