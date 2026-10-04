using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.IntegrationSupport;

/// <summary>在选定事件构造时注入故障，其他事件走真实序列化。</summary>
public sealed class RejectingEventSerializer : IIntegrationEventSerializer
{
    private readonly SystemTextJsonIntegrationEventSerializer _inner = new();

    /// <summary>需要拒绝的事件；空值表示正常序列化。</summary>
    public Func<IntegrationEvent, bool>? ShouldReject { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
        => ShouldReject?.Invoke(integrationEvent) is true
            ? throw new InvalidOperationException("测试事实序列化故障。") : _inner.Serialize(integrationEvent);

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
}
