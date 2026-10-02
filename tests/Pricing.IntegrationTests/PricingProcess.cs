using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Gateway;
using NexusStackNext.PricingHost;

namespace NexusStackNext.Pricing.IntegrationTests;

internal sealed class PricingProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private PricingProcess(Process process, Uri address)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        Client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
    }

    internal static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public HttpClient Client { get; }

    public static async Task<PricingProcess> StartAsync(string connectionString, bool worker = false, TimeSpan? leaseDuration = null)
    {
        var start = StartInfo(connectionString);
        start.Environment["Pricing__Worker__Enabled"] = worker.ToString();
        start.Environment["Pricing__Tasks__PollInterval"] = "00:00:00.100";
        if (leaseDuration is not null) { start.Environment["Pricing__Tasks__LeaseDuration"] = leaseDuration.Value.ToString("c", System.Globalization.CultureInfo.InvariantCulture); }
        return await StartHttpAsync(start);
    }

    public static Task<PricingProcess> StartGatewayAsync(string routePath)
    {
        var start = StartInfo(string.Empty);
        start.ArgumentList.Clear();
        start.ArgumentList.Add(typeof(GatewayHostMarker).Assembly.Location);
        start.Environment["Gateway__RouteTablePath"] = routePath;
        return StartHttpAsync(start);
    }

    private static async Task<PricingProcess> StartHttpAsync(ProcessStartInfo start)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_URLS"] = address.ToString();
        var app = new PricingProcess(Process.Start(start)!, address);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                Assert.False(app._process.HasExited, "Pricing 宿主提前退出；诊断输出保留在子进程，不回显凭据。");
                try
                {
                    using var response = await app.Client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);
                    if (response.IsSuccessStatusCode) { return app; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeout.Token);
            }
            throw new TimeoutException("Pricing 宿主未就绪。");
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

    public static ProcessStartInfo StartInfo(string connectionString)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(PricingHostMarker).Assembly.Location);
        start.Environment["ConnectionStrings__Pricing"] = connectionString;
        start.Environment["Jwt__SigningKey"] = SigningKey;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
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
