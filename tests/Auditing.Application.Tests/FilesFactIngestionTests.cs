using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Contracts;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class FilesFactIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("registered")]
    [InlineData("stored")]
    [InlineData("deletion-requested")]
    [InlineData("cleanup-deferred")]
    [InlineData("bytes-removed")]
    public async Task LifecycleFact_IsQueryableAndImmutable_AcrossRedelivery(string operation)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new FilesAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var message = Message(operation);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(message, serializer).ToEnvelope()));
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(message with { FileId = 801 }, serializer).ToEnvelope()));
        var fact = Assert.Single((await store.QueryAsync(new AuditQuery(1, 10) { Source = "files", SubjectType = "stored-file", SubjectId = "800" })).Entries).Fact;
        Assert.Equal("files.stored-file." + operation, fact.Action);
        Assert.Equal(2, fact.SubjectVersion);
        Assert.Null(fact.ActorId);
        Assert.Null(fact.RelatedSubject);
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    [Theory]
    [InlineData("deleted", 800, 2)]
    [InlineData("stored", 0, 2)]
    [InlineData("registered", -1, 1)]
    [InlineData("cleanup-deferred", 800, 0)]
    public async Task InvalidFact_DoesNotConsumeTheMessageIdentity(string operation, long id, long version)
    {
        var store = new InMemoryAuditEntryStore(new FixedClock(Now));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var consumer = new FilesAuditIngestion(new AuditIngestion(store, new SequentialIdGenerator(), new FixedClock(Now)), serializer);
        var valid = Message("registered");
        var invalid = valid with { Operation = operation, FileId = id, Version = version };
        Assert.False(await consumer.HandleAsync(OutboxEntry.From(invalid, serializer).ToEnvelope()));
        Assert.Empty((await store.QueryAsync(1, 10)).Entries);
        Assert.True(await consumer.HandleAsync(OutboxEntry.From(valid, serializer).ToEnvelope()));
        Assert.Single((await store.QueryAsync(1, 10)).Entries);
    }

    private static StoredFileCommittedV1 Message(string operation) => new()
    {
        FileId = 800,
        Operation = operation,
        Version = 2,
        OccurredAt = Now,
        TraceId = "file-trace",
        CorrelationId = "file-correlation",
    };
}
