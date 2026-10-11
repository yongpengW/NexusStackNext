using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;

namespace NexusStackNext.IntegrationSupport;

/// <summary>在真实 RabbitMQ 与测试宿主之间控制 TCP 断线；不解析 AMQP 载荷。</summary>
public sealed class RabbitMqFaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _pumps = [];
    private readonly RabbitMqOptions _upstream;
    private readonly Task _accept;
    private volatile bool _offline;

    /// <summary>启动连接到真实 broker 的本机 TCP 代理。</summary>
    /// <param name="upstream">真实 broker 配置。</param>
    public RabbitMqFaultProxy(RabbitMqOptions upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        _upstream = upstream;
        _listener.Start();
        _accept = AcceptAsync();
    }

    /// <summary>仅供测试宿主使用的代理连接配置。</summary>
    public RabbitMqOptions Options => _upstream with
    {
        HostName = "127.0.0.1",
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
    };

    /// <summary>切换代理是否拒绝并断开所有连接。</summary>
    /// <param name="offline">是否模拟断线。</param>
    public void SetOffline(bool offline)
    {
        _offline = offline;
        if (offline)
        {
            foreach (var client in _clients.Keys) { client.Dispose(); }
        }
    }

    /// <summary>等待代理至少建立一条完整的双向连接。</summary>
    /// <param name="timeout">等待上限。</param>
    public Task WaitForConnectionAsync(TimeSpan timeout) => WaitForStateAsync(static proxy => proxy._clients.Count >= 2, timeout);

    /// <summary>等待代理上的客户端与上游连接全部关闭。</summary>
    /// <param name="timeout">等待上限。</param>
    public Task WaitForDisconnectedAsync(TimeSpan timeout) => WaitForStateAsync(static proxy => proxy._clients.IsEmpty, timeout);

    private async Task WaitForStateAsync(Func<RabbitMqFaultProxy, bool> condition, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (!condition(this)) { await Task.Delay(25, cancellation.Token).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (!condition(this))
        {
            throw new TimeoutException($"RabbitMQ 故障代理在 {timeout} 内未达到预期连接状态。");
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (_offline) { client.Dispose(); continue; }
                _pumps.Add(PumpAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task PumpAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            _clients.TryAdd(client, 0);
            _clients.TryAdd(upstream, 0);
            try
            {
                await upstream.ConnectAsync(_upstream.HostName, _upstream.Port, _stop.Token).ConfigureAwait(false);
                var incoming = client.GetStream();
                var outgoing = upstream.GetStream();
                var clientToBroker = incoming.CopyToAsync(outgoing, _stop.Token);
                var brokerToClient = outgoing.CopyToAsync(incoming, _stop.Token);
                await Task.WhenAny(clientToBroker, brokerToClient).ConfigureAwait(false);
                client.Dispose();
                upstream.Dispose();
                await Task.WhenAll(clientToBroker, brokerToClient).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                client.Dispose();
                upstream.Dispose();
                _clients.TryRemove(client, out _);
                _clients.TryRemove(upstream, out _);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        SetOffline(true);
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_pumps).ConfigureAwait(false);
        _stop.Dispose();
    }
}
