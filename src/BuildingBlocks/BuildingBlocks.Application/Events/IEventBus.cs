using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// 事件总线。实现见票据 20（RabbitMQ 适配）。
/// <para>
/// <b>契约的核心是失败语义：</b>
/// </para>
/// <list type="bullet">
///   <item>「消息无法路由到任何队列」必须返回**失败**。参照仓库的做法是
///     <c>BasicReturn</c> 只记一条日志、发布后照样 ACK（<c>EventPublisher.cs:36-41/505-510</c>），
///     于是消息静默丢失，而调用方以为发成功了。它的设计文档还把该场景写成"可能无法路由"，
///     完全没说后果是"已 ACK 且消息丢失"。</item>
///   <item>失败必须携带可诊断的原因，因为它会被写进 Outbox 的 <c>LastFailure</c>。</item>
///   <item>实现不得吞掉异常——抛出即可，投递器会把异常当作失败处理。</item>
/// </list>
/// </summary>
public interface IEventBus
{
    /// <summary>发布一条消息。</summary>
    /// <param name="envelope">消息封套。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功或失败。</returns>
    Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}
