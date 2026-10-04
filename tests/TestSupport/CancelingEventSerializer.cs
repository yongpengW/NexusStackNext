using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.TestSupport;

/// <summary>在指定事实完成序列化后触发取消，验证提交边界。</summary>
/// <param name="inner">需要正常序列化时委托的适配器，由测试装配。</param>
public sealed class CancelingEventSerializer(IIntegrationEventSerializer inner) : IIntegrationEventSerializer
{
    /// <summary>本次序列化触发的取消源；空值表示正常序列化。</summary>
    public CancellationTokenSource? CancelOnSerialize { get; set; }

    /// <summary>需要取消的事件；不指定时对每次序列化触发。</summary>
    public Func<IntegrationEvent, bool>? ShouldCancel { get; set; }

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
    {
        var payload = inner.Serialize(integrationEvent);
        if (ShouldCancel?.Invoke(integrationEvent) ?? true) { CancelOnSerialize?.Cancel(); }
        return payload;
    }

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => inner.Deserialize<TEvent>(payload);
}
