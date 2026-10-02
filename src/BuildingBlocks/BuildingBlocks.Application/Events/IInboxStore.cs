namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// Inbox 去重存储（事务性收件箱）。
/// <para>
/// <b>必须在与业务改动同一个事务里调用。</b>这样"标记已处理"和"业务已生效"同生共死：
/// 中途崩溃会一起回滚，消息被重投时业务才会真正重做一次。若把标记放在另一个事务里，
/// 就会出现"标记了但业务没生效"的静默丢消息。
/// </para>
/// <para>
/// 投递语义因此是 <b>at-least-once + 消费端幂等</b>，不是 exactly-once——
/// 后者在分布式下不存在，声称它只会让人写出错误的假设。
/// </para>
/// <para>
/// <see cref="TryBeginProcessingAsync"/> 在业务事务内占键，成功一起提交，失败一起回滚。
/// 非事务存储的调用者才需要 <see cref="ReleaseAsync"/> 归还未完成的名额。
/// </para>
/// <para>
/// <b>调用顺序有讲究。</b>占键会占掉去重名额，
/// 因此**必须先完成纯校验、再调用它**——否则一条非法消息被拒之后，
/// 重投时会被判为已处理而**永久静默丢失**（票据 39 抓到过这个缺陷）。
/// </para>
/// <para>
/// 运输层不能在独立事务中先占名额；崩溃时来不及补偿，会静默跳过未完成业务。
/// RabbitMqConsumer 只依据应用处理器提交后的结果确认消息，不直接操作 Inbox。
/// </para>
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// 尝试登记一条待处理消息。
    /// </summary>
    /// <param name="consumerName">消费端名称。</param>
    /// <param name="eventName">事件名。</param>
    /// <param name="messageId">消息标识。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <c>true</c> 表示这是第一次收到该消息，可以处理；
    /// <c>false</c> 表示已处理过，<b>必须跳过</b>。
    /// </returns>
    Task<bool> TryBeginProcessingAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 归还一个已占用但**没有做成**的去重名额（幂等三段式的第二段）。
    /// <para>
    /// 只在"业务没有生效"的路径上调用：处理器返回失败、落库抛错。
    /// 业务成功时**不要**调用它——那会让同一条消息被重复执行。
    /// </para>
    /// <para>名额本来就不存在时（已成功留键、或从未占过）应当是无操作，不抛异常。</para>
    /// </summary>
    /// <param name="consumerName">消费端名称。</param>
    /// <param name="eventName">事件名。</param>
    /// <param name="messageId">消息标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task ReleaseAsync(
        string consumerName,
        string eventName,
        Guid messageId,
        CancellationToken cancellationToken = default);
}
