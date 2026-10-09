using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NexusStackNext.IntegrationSupport;

// Owns only loopback sockets for one journey database. Never records authentication or wire payloads.
// PostgreSQL CommandComplete('C', length, "COMMIT\0") proves the server completed COMMIT,
// before we close this connection without delivering that reply to the application.
internal sealed class PostgresCommitReplyFault : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Task> _connections = new();
    private readonly string _host;
    private readonly int _port;
    private readonly Task _accepting;
    private readonly TaskCompletionSource _dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    public PostgresCommitReplyFault(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        _host = target.Host!;
        _port = target.Port;
        _listener.Start(4);
        target.Host = "127.0.0.1";
        target.Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // This explicit test-only relay parses PostgreSQL framing, rather than changing production TLS.
        target.SslMode = SslMode.Disable;
        target.Pooling = false;
        target.Multiplexing = false;
        ConnectionString = target.ConnectionString;
        _accepting = AcceptAsync();
    }

    public string ConnectionString { get; }
    public void Arm() => Interlocked.Exchange(ref _armed, 1);
    public Task WaitForDroppedCommitAsync() => _dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Enqueue(ForwardAsync(incoming));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient incoming)
    {
        using (incoming)
        using (var upstream = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(_host, _port, connection.Token);
                var client = incoming.GetStream();
                var server = upstream.GetStream();
                var requests = client.CopyToAsync(server, connection.Token);
                var responses = ForwardResponsesAsync(server, client, connection.Token);
                await Task.WhenAny(requests, responses);
                await connection.CancelAsync();
                incoming.Close();
                upstream.Close();
                await Task.WhenAll(requests, responses).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task ForwardResponsesAsync(Stream server, Stream client, CancellationToken token)
    {
        var header = new byte[5];
        while (true)
        {
            await server.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > 16_777_216) { throw new IOException("Invalid test relay frame."); }
            var body = new byte[length - 4];
            await server.ReadExactlyAsync(body, token);
            if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _dropped.TrySetResult();
                return;
            }
            await client.WriteAsync(header, token);
            await client.WriteAsync(body, token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }
}
