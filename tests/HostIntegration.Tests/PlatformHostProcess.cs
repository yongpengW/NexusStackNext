using System.Diagnostics;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PlatformHost;

namespace NexusStackNext.HostIntegration.Tests;

internal sealed class PlatformHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _errors;
    private readonly ListeningAddress _listening = new();
    private readonly JourneyFileStorage _files = new();

    public HttpClient Client { get; }
    internal string FilesRoot { get; }

    private PlatformHostProcess(string connectionString, string? rootPassword, string? filesRoot, int cleanupBatchSize,
        IReadOnlyDictionary<string, string>? settings, Uri? listenAddress, HttpMessageHandler? httpHandler)
    {
        Client = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
        Client.Timeout = TimeSpan.FromSeconds(3);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(PlatformHostMarker).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(listenAddress?.AbsoluteUri ?? "http://127.0.0.1:0");
        start.Environment["Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime"] = "Information";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Identity__Storage__Provider"] = "Postgres";
        start.Environment["Platform__Storage__Provider"] = "Postgres";
        start.Environment["Files__Storage__Provider"] = "Postgres";
        start.Environment["Auditing__Storage__Provider"] = "Postgres";
        start.Environment["OperationJournal__Storage__Provider"] = "Postgres";
        start.Environment["Scheduling__Storage__Provider"] = "Postgres";
        start.Environment["Files__Cleanup__IntervalSeconds"] = "1";
        start.Environment["Files__Cleanup__RetryDelaySeconds"] = "1";
        start.Environment["Files__Cleanup__OrphanAgeSeconds"] = "1";
        start.Environment["Files__Cleanup__BatchSize"] = cleanupBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ConnectionStrings__Identity"] = connectionString;
        start.Environment["ConnectionStrings__Platform"] = connectionString;
        start.Environment["ConnectionStrings__Files"] = connectionString;
        start.Environment["ConnectionStrings__Auditing"] = connectionString;
        start.Environment["ConnectionStrings__OperationJournal"] = connectionString;
        start.Environment["ConnectionStrings__Scheduling"] = connectionString;
        start.Environment["Files__StorageRoot"] = filesRoot ?? _files.Root;
        start.Environment["Jwt__SigningKey"] = "integration-test-signing-key-long-enough-for-hs256";
        start.Environment["AgileConfig__AppId"] = string.Empty;
        start.Environment["RabbitMQ__HostName"] = string.Empty;
        start.Environment["Identity__Root__UserName"] = rootPassword is null ? string.Empty : "journey-root";
        start.Environment["Identity__Root__Password"] = rootPassword ?? string.Empty;
        if (settings is not null) { foreach (var (key, value) in settings) { start.Environment[key] = value; } }
        FilesRoot = start.Environment.TryGetValue("Files__StorageRoot", out var configuredRoot) && !string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot : Path.Combine(AppContext.BaseDirectory, "file-storage");
        _process = Process.Start(start)!;
        _output = _listening.CaptureAsync(_process.StandardOutput);
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static async Task<PlatformHostProcess> StartAsync(string connectionString, string? rootPassword = null, string? filesRoot = null,
        int cleanupBatchSize = 64, IReadOnlyDictionary<string, string>? settings = null, bool requireReady = true, Uri? listenAddress = null,
        HttpMessageHandler? httpHandler = null)
    {
        var host = new PlatformHostProcess(connectionString, rootPassword, filesRoot, cleanupBatchSize, settings, listenAddress, httpHandler);
        try
        {
            var elapsed = Stopwatch.StartNew();
            int? lastStatus = null;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(20) && !host._process.HasExited)
            {
                if (host.Client.BaseAddress is null && host._listening.Address is { } address)
                {
                    host.Client.BaseAddress = address;
                }
                if (host.Client.BaseAddress is null)
                {
                    await Task.Delay(100);
                    continue;
                }
                try
                {
                    using var ready = await host.Client.GetAsync(new Uri(requireReady ? "/health/ready" : "/health/live", UriKind.Relative));
                    lastStatus = (int)ready.StatusCode;
                    if (ready.IsSuccessStatusCode)
                    {
                        return host;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }

                await Task.Delay(100);
            }

            var exited = host._process.HasExited;
            int? exitCode = exited ? host._process.ExitCode : null;
            await host.CrashAsync();
            var diagnostic = Path.Combine(Path.GetTempPath(), $"nsn-platform-startup-{Guid.NewGuid():N}.log");
            await File.WriteAllTextAsync(diagnostic, (await host._output) + Environment.NewLine + (await host._errors));
            var failure = new InvalidOperationException($"平台子进程未就绪；exited={exited}; exitCode={exitCode}; lastStatus={lastStatus}。私有诊断：{diagnostic}；不回显日志内容。");
            failure.Data["DiagnosticPath"] = diagnostic;
            throw failure;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task CrashAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _ = await _output;
        _ = await _errors;
    }

    public async ValueTask DisposeAsync()
    {
        await CrashAsync();
        Client.Dispose();
        _process.Dispose();
        _files.Dispose();
    }
}
