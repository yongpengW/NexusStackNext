using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// <see cref="IInboxStore"/> 的契约。
/// <para>
/// <b>这是给实现者的规格，不是一个针对替身的测试。</b>票据 19 的 EF Core 实现必须继承这个类、
/// 提供自己的 <see cref="CreateStore"/>，从而<u>自动</u>接受同一套断言——把"消费端幂等"变成
/// 可机械继承的义务，而不是一句口号。
/// </para>
/// </summary>
public abstract class InboxStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MessageId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string EventName = "identity.user-registered.v1";

    /// <summary>创建被测实现。</summary>
    /// <returns>一个干净的存储实例。</returns>
    protected abstract IInboxStore CreateStore();

    [Fact]
    public async Task FirstDelivery_IsAccepted()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
    }

    [Fact]
    public async Task SameMessageTwice_SecondIsRejected()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.False(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
    }

    [Fact]
    public async Task SameMessage_DifferentConsumers_EachAcceptedOnce()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.True(await store.TryBeginProcessingAsync("scheduling", EventName, MessageId, Now));
        Assert.False(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.False(await store.TryBeginProcessingAsync("scheduling", EventName, MessageId, Now));
    }

    [Fact]
    public async Task SameMessageId_DifferentEvents_EachAcceptedOnce()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.True(await store.TryBeginProcessingAsync("auditing", "identity.user-disabled.v1", MessageId, Now));
    }

    [Fact]
    public async Task DifferentMessageIds_AllAccepted()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, Guid.NewGuid(), Now));
        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, Guid.NewGuid(), Now));
    }

    /// <summary>
    /// **三段式的第二段**：归还名额之后，同一条消息必须能再进来一次。
    /// <para>
    /// 缺了它，消费端的整条重试链是**安静的死的**：重投被判重复而 ACK 跳过，
    /// 重试档位与死信队列都轮不到——而计数器还在显示"重试过"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleasedMessage_IsAcceptedAgain()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.False(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));

        await store.ReleaseAsync("auditing", EventName, MessageId);

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
    }

    /// <summary>归还只影响那一个名额：别的消费者、别的事件都不受牵连。</summary>
    [Fact]
    public async Task Release_IsScopedToTheExactTriple()
    {
        var store = CreateStore();

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.True(await store.TryBeginProcessingAsync("scheduling", EventName, MessageId, Now));

        await store.ReleaseAsync("auditing", EventName, MessageId);

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
        Assert.False(await store.TryBeginProcessingAsync("scheduling", EventName, MessageId, Now));
    }

    /// <summary>从未占过的名额也能安全归还——"已成功留键"之后业务不会走到这里，但重复释放不该抛。</summary>
    [Fact]
    public async Task Release_OnUnknownMessage_IsANoOp()
    {
        var store = CreateStore();

        await store.ReleaseAsync("auditing", EventName, Guid.NewGuid());
        await store.ReleaseAsync("auditing", EventName, Guid.NewGuid());

        Assert.True(await store.TryBeginProcessingAsync("auditing", EventName, MessageId, Now));
    }
}

/// <summary>参照实现：内存版。EF Core 版见票据 19。</summary>
public sealed class InMemoryInboxStoreContractTests : InboxStoreContract
{
    protected override IInboxStore CreateStore() => new FakeInboxStore();
}
