using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.IntegrationSupport;

/// <summary>为载荷字节边界测试返回已知文本，不从生产计量实现推导期望值。</summary>
public sealed class KnownPayloadSerializer : IIntegrationEventSerializer
{
    private readonly SystemTextJsonIntegrationEventSerializer _inner = new();

    /// <summary>下一次序列化返回的已知文本。</summary>
    public string Payload { get; set; } = "中";

    /// <summary>限定替换的事件；其他事件仍使用真实 JSON 序列化。</summary>
    public Func<IntegrationEvent, bool>? UseKnownPayload { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
        => UseKnownPayload?.Invoke(integrationEvent) is false ? _inner.Serialize(integrationEvent) : Payload;

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
}
