using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NexusStackNext.TestSupport;

/// <summary>透明转发测试连接，只为下一份服务器响应注入有界传输延迟；不解析或记录载荷。</summary>
public sealed class TcpReplyDelayProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _pumps = [];
    private readonly string _host;
    private readonly int _port;
    private readonly Task _accept;
    private int _delayMilliseconds;
    private int _delayed;

    /// <summary>只监听本机随机端口，连接指定的测试上游。</summary>
    public TcpReplyDelayProxy(string host, int port)
    {
        _host = host;
        _port = port;
        _listener.Start();
        _accept = AcceptAsync();
    }

    /// <summary>客户端连接的本机端口。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>响应确实经过完整延迟后才被转发。</summary>
    public bool DelayedReply => Volatile.Read(ref _delayed) != 0;

    /// <summary>延迟下一份服务器响应，不改变后续响应或任何数据库设置。</summary>
    public void DelayNextReply(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay.TotalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay.TotalMilliseconds, 2000);
        Interlocked.Exchange(ref _delayMilliseconds, (int)delay.TotalMilliseconds);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _pumps.Add(PumpAsync(client));
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
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
                await upstream.ConnectAsync(_host, _port, _stop.Token);
                var incoming = client.GetStream();
                var outgoing = upstream.GetStream();
                var requests = incoming.CopyToAsync(outgoing, _stop.Token);
                var replies = ForwardRepliesAsync(outgoing, incoming);
                try { await Task.WhenAny(requests, replies); }
                finally
                {
                    upstream.Dispose();
                    client.Dispose();
                    await Task.WhenAll(requests, replies);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                _clients.TryRemove(client, out _);
                _clients.TryRemove(upstream, out _);
            }
        }
    }

    private async Task ForwardRepliesAsync(NetworkStream source, NetworkStream destination)
    {
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, _stop.Token)) != 0)
        {
            var delay = Interlocked.Exchange(ref _delayMilliseconds, 0);
            if (delay > 0)
            {
                await Task.Delay(delay, _stop.Token);
                Volatile.Write(ref _delayed, 1);
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
        }
    }

    /// <summary>取消转发并等待全部已接受连接退出，不遗留后台故障负载。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        foreach (var client in _clients.Keys) { client.Dispose(); }
        await Task.WhenAll(_pumps);
        _stop.Dispose();
    }
}
