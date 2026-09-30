using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// 基于 <c>System.Text.Json</c> 的集成事件序列化。
/// <para>
/// 接口在 <c>BuildingBlocks.Application.Events</c>，实现在这里——
/// 这就是这道缝的形状：换序列化格式不该改任何调用方。
/// </para>
/// </summary>
/// <param name="options">可选的序列化配置。</param>
public sealed class SystemTextJsonIntegrationEventSerializer(JsonSerializerOptions? options = null)
    : IIntegrationEventSerializer
{
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // 按运行时类型序列化：基类没有可序列化的成员，按基类序列化会丢掉全部字段。
        return JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), _options);
    }

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload)
        where TEvent : IntegrationEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return JsonSerializer.Deserialize<TEvent>(payload, _options)
            ?? throw new InvalidOperationException($"事件载荷反序列化得到 null：{typeof(TEvent).Name}。");
    }
}
