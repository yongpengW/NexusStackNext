namespace NexusStackNext.BuildingBlocks.Application.Events;

/// <summary>
/// 集成事件的序列化。写入 Outbox 时用一次，消费端反序列化时用一次。
/// <para>
/// <b>接口在这里，实现在 Infrastructure。</b>它是这道缝上的"变的东西"：
/// 换序列化格式（JSON → 别的）不该改任何调用方。
/// </para>
/// </summary>
public interface IIntegrationEventSerializer
{
    /// <summary>把事件序列化成载荷。</summary>
    /// <param name="integrationEvent">事件。</param>
    /// <returns>载荷文本。</returns>
    string Serialize(IntegrationEvent integrationEvent);

    /// <summary>按具体事件类型反序列化。</summary>
    /// <typeparam name="TEvent">事件类型。</typeparam>
    /// <param name="payload">载荷文本。</param>
    /// <returns>事件实例。</returns>
    TEvent Deserialize<TEvent>(string payload)
        where TEvent : IntegrationEvent;
}
