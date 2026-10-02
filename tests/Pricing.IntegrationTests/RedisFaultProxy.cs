using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.IntegrationTests;

// 只在测试 Redis 的 TCP 缝上控制断线/迟到请求；所有命令仍由真实 Redis 执行。
internal sealed class RedisFaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _pumps = [];
    private readonly EndPoint _upstream;
    private readonly Task _accept;
    private readonly TaskCompletionSource _fillPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFill = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pauseFill;
    private volatile bool _offline;

    public RedisFaultProxy(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        Assert.False(options.Ssl, "测试故障代理需要专用的非 TLS Redis；不降级产品连接。");
        _upstream = Assert.Single(options.EndPoints);
        _listener.Start();
        options.EndPoints.Clear();
        options.EndPoints.Add((IPEndPoint)_listener.LocalEndpoint);
        ConnectionString = options.ToString(includePassword: true);
        _accept = AcceptAsync();
    }

    public string ConnectionString { get; }
    public void PauseNextFill() => Interlocked.Exchange(ref _pauseFill, 1);
    public Task WaitForPausedFillAsync() => _fillPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void ReleaseFill() => _releaseFill.TrySetResult();
    public void SetOffline(bool offline)
    {
        _offline = offline;
        if (offline) { foreach (var client in _clients.Keys) { client.Dispose(); } }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (_offline) { client.Dispose(); continue; }
                _pumps.Add(PumpAsync(client));
            }
        }
        catch (OperationCanceledException) { }
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
                if (_upstream is DnsEndPoint dns) { await upstream.ConnectAsync(dns.Host, dns.Port, _stop.Token); }
                else { var ip = (IPEndPoint)_upstream; await upstream.ConnectAsync(ip.Address, ip.Port, _stop.Token); }
                var incoming = client.GetStream();
                var outgoing = upstream.GetStream();
                var replies = outgoing.CopyToAsync(incoming, _stop.Token);
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var command = await ReadCommandAsync(incoming, _stop.Token);
                        if (command is null) { break; }
                        // EVALSHA 可能命中另一连接已加载的脚本；两种形式都按回填载荷定位。
                        if (command.Value.Arguments.Any(x => x.Contains("nsn-pricing-fill", StringComparison.Ordinal)
                                || x.StartsWith("{\"ItemId\":", StringComparison.Ordinal))
                            && Interlocked.Exchange(ref _pauseFill, 0) == 1)
                        {
                            _fillPaused.TrySetResult();
                            await _releaseFill.Task.WaitAsync(_stop.Token);
                        }
                        await outgoing.WriteAsync(command.Value.Bytes, _stop.Token);
                    }
                }
                finally { upstream.Dispose(); client.Dispose(); await replies; }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { _clients.TryRemove(client, out _); _clients.TryRemove(upstream, out _); }
        }
    }

    private static async Task<(byte[] Bytes, string[] Arguments)?> ReadCommandAsync(NetworkStream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var header = await ReadLineAsync(stream, bytes, token);
        if (header is null) { return null; }
        if (!header.StartsWith('*')) { throw new IOException("测试代理只接受 RESP 数组命令。"); }
        var count = int.Parse(header.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
        var arguments = new string[count];
        for (var i = 0; i < count; i++)
        {
            var bulk = await ReadLineAsync(stream, bytes, token) ?? throw new EndOfStreamException();
            var length = int.Parse(bulk.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            var content = new byte[length + 2];
            await stream.ReadExactlyAsync(content, token);
            bytes.Write(content);
            arguments[i] = Encoding.UTF8.GetString(content, 0, length);
        }
        return (bytes.ToArray(), arguments);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, MemoryStream bytes, CancellationToken token)
    {
        using var line = new MemoryStream();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, token) != 0)
        {
            bytes.WriteByte(buffer[0]);
            if (buffer[0] == '\n') { return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'); }
            line.WriteByte(buffer[0]);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        SetOffline(true);
        await _accept;
        await Task.WhenAll(_pumps);
        _stop.Dispose();
    }
}
