namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// 事务性收件箱的一条记录：**"这条消息已经处理过了"这个事实本身**。
///
/// <para><b>它与业务改动在同一个事务里写入</b>（见 <c>IInboxStore</c> 的说明）。
/// 这样"标记已处理"和"业务已生效"同生共死——中途崩溃会一起回滚，
/// 消息被重投时业务才会真正重做一次。若把标记放在另一个事务里，
/// 就会出现"标记了但业务没生效"的**静默丢消息**。</para>
///
/// <para><b>主键就是去重键</b>：<c>(ConsumerName, EventName, MessageId)</c>。
/// 用主键而不是另加一个唯一索引，是因为"同一条消息在同一个消费端只能登记一次"
/// 就是这张表的身份——不需要第二处约束去表达同一件事。</para>
///
/// <para>它住在基础设施层而**不是领域层**：去重是投递语义的实现细节，
/// 领域里没有任何概念对应它。</para>
/// </summary>
public sealed class InboxMessage
{
    /// <summary>消费端名称。同一个事件的不同消费端各自去重。</summary>
    public required string ConsumerName { get; set; }

    /// <summary>稳定、带版本的事件名。</summary>
    public required string EventName { get; set; }

    /// <summary>消息标识。</summary>
    public required Guid MessageId { get; set; }

    /// <summary>首次登记时刻（UTC）。</summary>
    public required DateTimeOffset ReceivedAt { get; set; }
}
