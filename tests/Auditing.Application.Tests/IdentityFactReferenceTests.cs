using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class IdentityFactReferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("refresh-token", "consumed", "user")]
    [InlineData("user", "role-assigned", "role")]
    [InlineData("user", "role-revoked", "role")]
    [InlineData("role", "menu-granted", "menu")]
    [InlineData("role", "menu-revoked", "menu")]
    [InlineData("menu-tree", "node-added", "menu")]
    [InlineData("menu-tree", "node-removed", "menu")]
    [InlineData("menu-tree", "node-moved", "menu")]
    [InlineData("menu-tree", "node-ancestry-changed", "menu")]
    [InlineData("menu-tree", "node-renamed", "menu")]
    [InlineData("menu-tree", "node-reordered", "menu")]
    [InlineData("api-resource", "registered", "menu")]
    public async Task RelatedSubject_IsQueryable_AndCannotChangeOnRedelivery(string subject, string operation, string relatedType)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new IdentityAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message(subject, operation, new(relatedType, 811));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { RelatedSubject = new(relatedType, 812) }, serializer).ToEnvelope()));
        var query = new AuditQuery(1, 10) { RelatedContext = "identity", RelatedSubjectType = relatedType, RelatedSubjectId = "811" };
        var fact = Assert.Single((await store.QueryAsync(query)).Entries).Fact;
        Assert.Equal(new AuditSubjectReference("identity", relatedType, "811"), fact.RelatedSubject);
        Assert.Empty((await store.QueryAsync(query with { RelatedContext = "other" })).Entries);
        Assert.Empty((await store.QueryAsync(query with { RelatedSubjectType = "other" })).Entries);
        Assert.Empty((await store.QueryAsync(query with { RelatedSubjectId = "812" })).Entries);
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Theory]
    [InlineData("refresh-token", "issued", null, 0)]
    [InlineData("refresh-token", "revoked", "user", -1)]
    [InlineData("refresh-token", "consumed", "role", 811)]
    [InlineData("user", "role-assigned", "user", 811)]
    [InlineData("user", "role-revoked", "role", 0)]
    [InlineData("role", "menu-granted", null, 0)]
    [InlineData("role", "menu-revoked", "user", 811)]
    [InlineData("user", "created", "role", 811)]
    [InlineData("menu-tree", "node-added", null, 0)]
    [InlineData("menu-tree", "node-moved", "menu", 0)]
    [InlineData("menu-tree", "node-renamed", "role", 811)]
    [InlineData("menu-tree", "created", "menu", 811)]
    [InlineData("api-resource", "registered", "menu", -1)]
    [InlineData("api-resource", "registered", "user", 811)]
    public async Task InvalidActionAndReferencePair_IsRejectedWithoutConsumingIdentity(string subject, string operation, string? relatedType, long relatedId)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new IdentityAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var invalid = Message(subject, operation, relatedType is null ? null : new(relatedType, relatedId));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(invalid, serializer).ToEnvelope()));
        Assert.Empty((await store.QueryAsync(1, 10)).Entries);
        var corrected = Message("refresh-token", "issued", new("user", 811)) with { EventId = invalid.EventId };
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(corrected, serializer).ToEnvelope()));
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Fact]
    public async Task ResourceWithoutAMenu_IsAcceptedWithoutInventingARelationship()
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new IdentityAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message("api-resource", "registered", null);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.Null(Assert.Single((await store.QueryAsync(1, 10)).Entries).Fact.RelatedSubject);
    }

    private static IdentityEntityCommittedV1 Message(string subject, string operation, IdentitySubjectReference? related) => new()
    {
        SubjectType = subject,
        SubjectId = "800",
        Operation = operation,
        Version = 2,
        OccurredAt = Now,
        TraceId = "related-trace",
        CorrelationId = "related-correlation",
        RelatedSubject = related,
    };
}
