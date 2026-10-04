using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.TestSupport;

/// <summary>为载荷字节边界测试返回已知文本，不从生产计量实现推导期望值。</summary>
/// <param name="inner">需要正常序列化时委托的适配器，由测试装配。</param>
public sealed class KnownPayloadSerializer(IIntegrationEventSerializer inner) : IIntegrationEventSerializer
{
    /// <summary>下一次序列化返回的已知文本。</summary>
    public string Payload { get; set; } = "中";

    /// <summary>限定替换的事件；其他事件仍使用真实 JSON 序列化。</summary>
    public Func<IntegrationEvent, bool>? UseKnownPayload { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
        => UseKnownPayload?.Invoke(integrationEvent) is false ? inner.Serialize(integrationEvent) : Payload;

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => inner.Deserialize<TEvent>(payload);
}
