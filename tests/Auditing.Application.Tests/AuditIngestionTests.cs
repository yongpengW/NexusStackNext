using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

public sealed class AuditIngestionTests
{
    [Fact]
    public async Task InvestigationOrdersByReceiptTime_EvenWhenIdsWereAllocatedInAnotherOrder()
    {
        var entries = new InMemoryAuditEntryStore(new FixedClock(Now));
        var older = new AuditIngestion(entries, new SequentialIdGenerator(9000), new FixedClock(Now));
        var newer = new AuditIngestion(entries, new SequentialIdGenerator(1000), new FixedClock(Now.AddSeconds(1)));
        var first = Message();
        var second = Message();
        await older.IngestAsync(first);
        await newer.IngestAsync(second);
        Assert.Equal(second.MessageId, Assert.Single((await entries.QueryAsync(1, 1)).Entries).Fact.MessageId);
        Assert.Equal(first.MessageId, Assert.Single((await entries.QueryAsync(2, 1)).Entries).Fact.MessageId);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static AuditFact Message(Guid? messageId = null, string action = "platform.setting.changed") =>
        new(messageId ?? Guid.NewGuid(), "platform.setting-committed.v1", "platform", action,
            "global-setting", "1001", 2, "actor-1", Now.AddMinutes(-1), "trace-1", "correlation-1");

    private static (AuditIngestion Ingestion, InMemoryAuditEntryStore Entries) NewIngestion()
    {
        var entries = new InMemoryAuditEntryStore(new FixedClock(Now));
        return (new AuditIngestion(entries, new SequentialIdGenerator(5000), new FixedClock(Now)), entries);
    }

    [Fact]
    public async Task ReusingMessageIdentityWithDifferentContent_IsRejected()
    {
        var (ingestion, entries) = NewIngestion();
        var original = Message();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(original)).Value);
        var conflicting = await ingestion.IngestAsync(original with { SubjectId = "999" });
        Assert.True(conflicting.IsFailure);
        Assert.Equal("auditing.message_conflict", conflicting.Error.Code);
        Assert.Equal(original, Assert.Single((await entries.QueryAsync(1, 50)).Entries).Fact);
    }

    [Fact]
    public async Task FirstDelivery_IsAcceptedAndPreservesFactAndReceiptTimes()
    {
        var (ingestion, entries) = NewIngestion();
        var message = Message();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(message)).Value);
        var entry = Assert.Single((await entries.QueryAsync(1, 50)).Entries);
        Assert.Equal(message, entry.Fact);
        Assert.Equal(Now, entry.RecordedAt);
    }

    [Fact]
    public async Task RedeliveringTheSameMessage_IsDuplicateAndWritesNothing()
    {
        var (ingestion, entries) = NewIngestion();
        var message = Message();
        await ingestion.IngestAsync(message);
        Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(message)).Value);
        Assert.Single((await entries.QueryAsync(1, 50)).Entries);
    }

    [Fact]
    public async Task SameContentDifferentMessageId_IsAcceptedTwice()
    {
        var (ingestion, entries) = NewIngestion();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(Message())).Value);
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(Message())).Value);
        Assert.Equal(2, (await entries.QueryAsync(1, 50)).Total);
    }

    [Fact]
    public async Task RejectedMessage_DoesNotConsumeTheDedupeSlot()
    {
        var (ingestion, entries) = NewIngestion();
        var messageId = Guid.NewGuid();
        Assert.True((await ingestion.IngestAsync(Message(messageId, action: "   "))).IsFailure);
        Assert.Empty((await entries.QueryAsync(1, 50)).Entries);
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(Message(messageId))).Value);
        Assert.Single((await entries.QueryAsync(1, 50)).Entries);
    }

    [Fact]
    public async Task DifferentEventNames_DoNotCollideOnTheSameMessageId()
    {
        var (ingestion, entries) = NewIngestion();
        var first = Message();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(first)).Value);
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(first with { EventName = "platform.setting-committed.v2" })).Value);
        Assert.Equal(2, (await entries.QueryAsync(1, 50)).Total);
    }

    [Fact]
    public async Task NullMessage_IsRejected()
    {
        var (ingestion, _) = NewIngestion();
        await Assert.ThrowsAsync<ArgumentNullException>(() => ingestion.IngestAsync(null!));
    }

    [Theory]
    [InlineData("", "user", "1")]
    [InlineData("act", "", "1")]
    [InlineData("act", "user", "")]
    public async Task MalformedSubjectOrAction_IsRejectedWithoutRecording(string action, string subjectType, string subjectId)
    {
        var (ingestion, entries) = NewIngestion();
        Assert.True((await ingestion.IngestAsync(Message(action: action) with { SubjectType = subjectType, SubjectId = subjectId })).IsFailure);
        Assert.Empty((await entries.QueryAsync(1, 50)).Entries);
    }
}
