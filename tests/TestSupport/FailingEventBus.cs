using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.TestSupport;

/// <summary>通过事件总线公开接口稳定拒绝发布，让真实投递器执行停止与恢复协议。</summary>
public sealed class FailingEventBus : IEventBus
{
    /// <inheritdoc />
    public Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result.Failure(new Error("broker.controlled_failure", "Controlled publish failure.")));
    }
}
