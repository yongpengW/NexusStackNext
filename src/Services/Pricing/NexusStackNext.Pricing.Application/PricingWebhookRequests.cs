using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Pricing.Application;

/// <summary>读取 Pricing 重算通知状态。</summary>
public sealed record GetPricingWebhook(Guid EventId) : IQuery<PricingWebhookStatus>;

/// <summary>按观察到的尝试次数恢复失败的通知。</summary>
public sealed record RetryPricingWebhook(Guid EventId, int ExpectedAttemptCount) : ICommand<PricingWebhookStatus>;

/// <summary>外部通知的持久状态。</summary>
public sealed record PricingWebhookStatus(Guid EventId, Guid ItemId, string State, int AttemptCount,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeliveredAt,
    int? LastStatusCode, string? LastError, string TraceId, string CorrelationId);

/// <summary>受控外部接收端的最小发送端口。</summary>
public interface IPricingWebhookSender
{
    /// <summary>发送一个带稳定幂等键的通知。</summary>
    Task<PricingWebhookSendResult> SendAsync(PricingWebhookSendRequest request, CancellationToken cancellationToken = default);
}

/// <summary>发送给外部接收端的脱敏请求。</summary>
public sealed record PricingWebhookSendRequest(Uri Endpoint, string Payload, Guid IdempotencyKey, string TraceId);

/// <summary>外部发送结果；响应正文不进入业务状态。</summary>
public sealed record PricingWebhookSendResult(bool Succeeded, bool Retryable, int? StatusCode, string? ErrorCode);

/// <summary>触发一轮待投递通知。</summary>
public interface IPricingWebhookDeliveryRunner
{
    /// <summary>发送当前到期的通知并返回本轮成功数。</summary>
    Task<int> DeliverPendingAsync(CancellationToken cancellationToken = default);
}
