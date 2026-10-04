using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.TestSupport;

/// <summary>在指定事件的真实编码完成后暂停一次返回，安排消息登记与外部维护的重叠。</summary>
/// <param name="inner">实际编码适配器。</param>
/// <param name="eventName">只暂停这个线路事件；其他事实继续正常编码。</param>
public sealed class PausingEventSerializer(IIntegrationEventSerializer inner, string eventName) : IIntegrationEventSerializer, IDisposable
{
    private readonly ManualResetEventSlim _resume = new(false);
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    /// <summary>事件已完成编码，调用者尚未收到编码结果。</summary>
    public Task Paused => _paused.Task;

    /// <summary>释放暂停；测试必须在 finally 中释放并等待操作完成。</summary>
    public void Resume() => _resume.Set();

    /// <inheritdoc />
    public string Serialize(IntegrationEvent integrationEvent)
    {
        var payload = inner.Serialize(integrationEvent);
        if (integrationEvent.EventName == eventName && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _paused.TrySetResult();
            if (!_resume.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("测试消息编码未被释放。"); }
        }
        return payload;
    }

    /// <inheritdoc />
    public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => inner.Deserialize<TEvent>(payload);

    /// <inheritdoc />
    public void Dispose() => _resume.Dispose();
}
