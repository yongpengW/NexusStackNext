namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>集成事件接纳端口。实现负责在自己的事务中提交 Inbox、业务修改与工作登记。</summary>
public interface IIntegrationEventProcessor
{
    /// <summary>显式版本化的事件名。</summary>
    string EventName { get; }

    /// <summary>接纳事件。暂时故障抛异常，运输层保留原消息并退避重投。</summary>
    /// <param name="envelope">稳定消息标识与载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已提交处理结论（含持久业务拒绝）或匹配的重复消息为 true；无效信封或内容冲突为 false，按订阅进入重试或死信。</returns>
    Task<bool> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}
