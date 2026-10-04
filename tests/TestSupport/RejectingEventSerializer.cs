using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.TestSupport;

/// <summary>在选定事件构造时注入故障，其他事件走真实序列化。</summary>
/// <param name="inner">需要正常序列化时委托的适配器，由测试装配。</param>
public sealed class RejectingEventSerializer(IIntegrationEventSerializer inner) : IIntegrationEventSerializer
{
    /// <summary>需要拒绝的事件；空值表示正常序列化。</summary>
    public Func<IntegrationEvent, bool>? ShouldReject { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
        => ShouldReject?.Invoke(integrationEvent) is true
            ? throw new InvalidOperationException("测试事实序列化故障。") : inner.Serialize(integrationEvent);

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => inner.Deserialize<TEvent>(payload);
}
