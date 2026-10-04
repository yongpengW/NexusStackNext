using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;

namespace NexusStackNext.IntegrationSupport;

/// <summary>在指定事实完成序列化后触发取消，验证提交边界。</summary>
public sealed class CancelingEventSerializer : IIntegrationEventSerializer
{
    private readonly SystemTextJsonIntegrationEventSerializer _inner = new();

    /// <summary>本次序列化触发的取消源；空值表示正常序列化。</summary>
    public CancellationTokenSource? CancelOnSerialize { get; set; }

    /// <summary>需要取消的事件；不指定时对每次序列化触发。</summary>
    public Func<IntegrationEvent, bool>? ShouldCancel { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
    {
        var payload = _inner.Serialize(integrationEvent);
        if (ShouldCancel?.Invoke(integrationEvent) ?? true) { CancelOnSerialize?.Cancel(); }
        return payload;
    }

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
}
