using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>两个来源共同使用的固定期限凭据准备与 UTF-8 计量，不执行存储发布。</summary>
public static class FactDeliveryRecoveryPreparation
{
    /// <summary>准备不可变原裁决；调用者只在所属存储的条件比较成功后原子发布。</summary>
    /// <param name="request">已校验的稳定请求。</param>
    /// <param name="source">模块代码声明的来源。</param>
    /// <param name="actorId">可信操作者。</param>
    /// <param name="occurredAt">可信接受时刻。</param>
    /// <param name="execution">可信执行关联。</param>
    /// <returns>原裁决、其持久 JSON 和 UTF-8 字节数。</returns>
    public static PreparedRecovery Prepare(FactDeliveryRecoveryRequest request, string source, string actorId,
        DateTimeOffset occurredAt, ExecutionOrigin? execution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!request.IsValidFor(actorId, occurredAt, execution))
        { throw new ArgumentException("恢复裁决输入无效。", nameof(request)); }
        var receipt = new FactDeliveryRecoveryReceipt(request.RequestId, request.MessageId, source, actorId,
            request.Reason, request.ExpectedDeadLetteredAt, request.ExpectedRetryRevision, checked(request.ExpectedRetryRevision + 1),
            occurredAt, occurredAt.AddDays(7), execution);
        var json = JsonSerializer.Serialize(receipt);
        return new(receipt, json, Encoding.UTF8.GetByteCount(json));
    }

    /// <summary>全部准备成功后才允许所属适配器进行原子发布。</summary>
    /// <param name="Receipt">固定原裁决。</param>
    /// <param name="Json">原裁决持久表示。</param>
    /// <param name="PayloadBytes">UTF-8 字节数。</param>
    public sealed record PreparedRecovery(FactDeliveryRecoveryReceipt Receipt, string Json, int PayloadBytes);
}
