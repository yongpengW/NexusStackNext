using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.BuildingBlocks.Application.Security;
using Xunit;

namespace NexusStackNext.IntegrationSupport;

internal sealed class BusinessProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private BusinessProcess(Process process, Uri address)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        Client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
    }

    internal static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public HttpClient Client { get; }

    public static async Task<BusinessProcess> StartAsync(string assembly, string context, string connectionString, bool worker = false, TimeSpan? leaseDuration = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        var start = StartInfo(assembly, context, connectionString);
        start.Environment[$"{context}__Worker__Enabled"] = worker.ToString();
        start.Environment[$"{context}__Tasks__PollInterval"] = "00:00:00.100";
        if (leaseDuration is not null) { start.Environment[$"{context}__Tasks__LeaseDuration"] = leaseDuration.Value.ToString("c", System.Globalization.CultureInfo.InvariantCulture); }
        if (settings is not null) { foreach (var (key, value) in settings) { start.Environment[key] = value; } }
        return await StartHttpAsync(start);
    }

    public static Task<BusinessProcess> StartGatewayAsync(string assembly, string routePath)
    {
        var start = StartInfo(assembly, "Gateway", string.Empty);
        start.ArgumentList.Clear();
        start.ArgumentList.Add(assembly);
        start.Environment["Gateway__RouteTablePath"] = routePath;
        return StartHttpAsync(start);
    }

    private static async Task<BusinessProcess> StartHttpAsync(ProcessStartInfo start)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_URLS"] = address.ToString();
        var app = new BusinessProcess(Process.Start(start)!, address);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                Assert.False(app._process.HasExited, "业务宿主提前退出；诊断输出保留在子进程，不回显凭据。");
                try
                {
                    using var response = await app.Client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);
                    if (response.IsSuccessStatusCode) { return app; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeout.Token);
            }
            throw new TimeoutException("业务宿主未就绪。");
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public void Authenticate(bool root = true)
    {
        var claims = new List<Claim> { new("sub", "test-operator") };
        if (root) { claims.Add(new Claim(NexusStackClaims.Root, "true")); }
        var token = new JwtSecurityToken("nexusstack", "nexusstack", claims, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    public static ProcessStartInfo StartInfo(string assembly, string context, string connectionString)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(assembly);
        start.Environment[$"ConnectionStrings__{context}"] = connectionString;
        start.Environment["Jwt__SigningKey"] = SigningKey;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["OperationJournal__Storage__Provider"] = "Memory";
        start.Environment["AgileConfig__AppId"] = string.Empty;
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_process.HasExited) { _process.Kill(entireProcessTree: true); }
        await _process.WaitForExitAsync();
        await Task.WhenAll(_output, _error);
        _process.Dispose();
    }

    public static async Task<(int ExitCode, string Output)> RunToExitAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        return (process.ExitCode, await output + await error);
    }
}
