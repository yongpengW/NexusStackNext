using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NexusStackNext.PlatformHost;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class PlatformHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _errors;

    public HttpClient Client { get; }

    private PlatformHostProcess(string connectionString, string? rootPassword)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(PlatformHostMarker).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(Client.BaseAddress.ToString());
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Identity__Storage__Provider"] = "Postgres";
        start.Environment["Platform__Storage__Provider"] = "Postgres";
        start.Environment["ConnectionStrings__Identity"] = connectionString;
        start.Environment["ConnectionStrings__Platform"] = connectionString;
        start.Environment["Jwt__SigningKey"] = "integration-test-signing-key-long-enough-for-hs256";
        start.Environment["AgileConfig__AppId"] = string.Empty;
        start.Environment["RabbitMQ__HostName"] = string.Empty;
        start.Environment["Identity__Root__UserName"] = rootPassword is null ? string.Empty : "journey-root";
        start.Environment["Identity__Root__Password"] = rootPassword ?? string.Empty;
        _process = Process.Start(start)!;
        _output = _process.StandardOutput.ReadToEndAsync();
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static async Task<PlatformHostProcess> StartAsync(string connectionString, string? rootPassword = null)
    {
        var host = new PlatformHostProcess(connectionString, rootPassword);
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(20) && !host._process.HasExited)
            {
                try
                {
                    using var ready = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
                    if (ready.IsSuccessStatusCode)
                    {
                        return host;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }

                await Task.Delay(100);
            }

            throw new InvalidOperationException("平台子进程未就绪；不回显可能包含敏感信息的宿主日志。");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _ = await _output;
        _ = await _errors;
        Client.Dispose();
        _process.Dispose();
    }
}
