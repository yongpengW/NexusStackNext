using System.Collections.Concurrent;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Auditing.Application.Tests;

/// <summary>
/// 审计摄取：幂等语义与失败路径。
/// <para>
/// 这一层用运行时验证覆盖过分支，但有一类情形 HTTP 层面构造不出来——
/// **"被拒绝的消息会不会占掉去重名额"**。它决定了非法消息是被拒（可重投）
/// 还是被永久静默丢弃。
/// </para>
/// </summary>
public sealed class AuditIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string EventName = "audit.recorded.v1";

    private static AuditIngestedMessage Message(
        Guid? messageId = null,
        string action = "identity.user-registered",
        string eventName = EventName) =>
        new(messageId ?? Guid.NewGuid(), eventName, action, "user", "1001", "actor-1", "{}");

    private static (AuditIngestion Ingestion, RecordingAuditEntryStore Entries) NewIngestion()
    {
        var entries = new RecordingAuditEntryStore();
        var ingestion = new AuditIngestion(
            new InMemoryInboxStore(),
            entries,
            new SequentialIdGenerator(5000),
            new FixedClock(Now));

        return (ingestion, entries);
    }

    [Fact]
    public async Task FirstDelivery_IsAcceptedAndRecorded()
    {
        var (ingestion, entries) = NewIngestion();

        var result = await ingestion.IngestAsync(Message());

        Assert.Equal(IngestionOutcome.Accepted, result.Value);
        var entry = Assert.Single(entries.Saved);
        Assert.Equal("identity.user-registered", entry.Action);
    }

    [Fact]
    public async Task RedeliveringTheSameMessage_IsDuplicateAndWritesNothing()
    {
        var (ingestion, entries) = NewIngestion();
        var message = Message();

        await ingestion.IngestAsync(message);
        var second = await ingestion.IngestAsync(message);

        Assert.Equal(IngestionOutcome.Duplicate, second.Value);
        Assert.Single(entries.Saved);
    }

    [Fact]
    public async Task SameContentDifferentMessageId_IsAcceptedTwice()
    {
        // 幂等键是**消息标识**，不是业务标识。
        // 绑业务标识的后果（参照仓库 review/04 F7）：两次合法的相同操作会被当成重复，丢掉一次。
        var (ingestion, entries) = NewIngestion();

        var first = await ingestion.IngestAsync(Message());
        var second = await ingestion.IngestAsync(Message());

        Assert.Equal(IngestionOutcome.Accepted, first.Value);
        Assert.Equal(IngestionOutcome.Accepted, second.Value);
        Assert.Equal(2, entries.Saved.Count);
    }

    [Fact]
    public async Task RejectedMessage_DoesNotConsumeTheDedupeSlot()
    {
        // 这条是整组测试里最重要的一条。
        //
        // 如果"标记已处理"发生在聚合校验**之前**，一条非法消息会占掉去重名额：
        // 它被拒了，但再次投递时会被判为 Duplicate —— 消息**永久静默丢失**，
        // 而且从任何地方都看不出来。这正是 IInboxStore 文档警告的那种失败。
        var (ingestion, entries) = NewIngestion();
        var messageId = Guid.NewGuid();

        var rejected = await ingestion.IngestAsync(Message(messageId, action: "   "));
        Assert.True(rejected.IsFailure);
        Assert.Empty(entries.Saved);

        // 同一条消息（同一个标识）修好后重投：**必须仍然算首次**。
        var retried = await ingestion.IngestAsync(Message(messageId));

        Assert.Equal(IngestionOutcome.Accepted, retried.Value);
        Assert.Single(entries.Saved);
    }

    [Fact]
    public async Task TwoConsumers_EachProcessTheSameMessageOnce()
    {
        // 去重按"消费端 + 事件名 + 消息标识"三者一起判：
        // 同一个事件被两个消费端各自处理一次是正常的，不该互相顶掉。
        var inbox = new InMemoryInboxStore();
        var now = Now;
        var messageId = Guid.NewGuid();

        Assert.True(await inbox.TryBeginProcessingAsync("auditing.entries", EventName, messageId, now));
        Assert.True(await inbox.TryBeginProcessingAsync("other.consumer", EventName, messageId, now));
        Assert.False(await inbox.TryBeginProcessingAsync("auditing.entries", EventName, messageId, now));
    }

    [Fact]
    public async Task DifferentEventNames_DoNotCollideOnTheSameMessageId()
    {
        var inbox = new InMemoryInboxStore();
        var messageId = Guid.NewGuid();

        Assert.True(await inbox.TryBeginProcessingAsync("auditing.entries", "a.v1", messageId, Now));
        Assert.True(await inbox.TryBeginProcessingAsync("auditing.entries", "b.v1", messageId, Now));
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

        var message = new AuditIngestedMessage(Guid.NewGuid(), EventName, action, subjectType, subjectId, null, null);
        var result = await ingestion.IngestAsync(message);

        Assert.True(result.IsFailure);
        Assert.Empty(entries.Saved);
    }

    /// <summary>记下写入内容的审计存储替身。</summary>
    private sealed class RecordingAuditEntryStore : IAuditEntryStore
    {
        private readonly ConcurrentQueue<AuditEntry> _saved = new();

        public IReadOnlyList<AuditEntry> Saved => [.. _saved];

        public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            _saved.Enqueue(entry);
            return Task.CompletedTask;
        }
    }

}
