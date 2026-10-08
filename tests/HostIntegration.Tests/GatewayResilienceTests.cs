using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NexusStackNext.Gateway;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class GatewayResilienceTests
{
    [Fact]
    public async Task AnonymousQuota_CannotBeBypassedByChangingForwardedIpOrInvalidTokens()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        gateway.UseRoutes([new RouteDefinition
        {
            RouteId = "anonymous", ClusterId = "backend", Path = "/anonymous/{**rest}",
            RequireAuthentication = false, RateLimitPolicy = "gateway.default",
        }]);
        using var client = gateway.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/anonymous/{i}", UriKind.Relative));
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"192.0.2.{i + 1}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"invalid-token-{i}");
            using var response = await client.SendAsync(request);
            Assert.Equal(i < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(string.Empty));
        using var withoutSubject = await client.GetAsync(new Uri("/anonymous/without-subject", UriKind.Relative));
        Assert.Equal(HttpStatusCode.TooManyRequests, withoutSubject.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("verified-user"));
        using var authenticated = await client.GetAsync(new Uri("/anonymous/verified", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task ShippedFilesRoute_EnforcesRateLimiting()
    {
        await using var backend = await GatewayBackend.StartAsync("files");
        await using var gateway = new GatewayHttpApp(backend.Address);
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
        var files = Assert.Single(shipped.Routes, route => route.RouteId == "files-api");
        gateway.UseRoutes([files with { ClusterId = "backend" }]);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("files-user"));
        for (var i = 0; i < 2; i++)
        {
            using var allowed = await client.GetAsync(new Uri("/api/files/example", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await client.GetAsync(new Uri("/api/files/example", UriKind.Relative));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Theory]
    [InlineData("failureThreshold", "0")]
    [InlineData("interval", "\"00:00:00\"")]
    [InlineData("timeout", "\"00:00:00\"")]
    [InlineData("path", "\"relative\"")]
    public async Task InvalidHealthSettings_RejectStartup(string property, string jsonValue)
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(gateway.RouteTablePath))!;
        document["clusters"]![0]!["healthCheck"]![property] = JsonNode.Parse(jsonValue);
        await File.WriteAllTextAsync(gateway.RouteTablePath, document.ToJsonString());

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = gateway.CreateClient();
        });
    }

    [Fact]
    public async Task GatewayReadiness_UsesConfiguredDownstreamReadiness_WhileLivenessStaysHealthy()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        var file = await File.ReadAllTextAsync(gateway.RouteTablePath);
        await File.WriteAllTextAsync(gateway.RouteTablePath, file.Replace("/health/ready", "/custom/ready", StringComparison.Ordinal));
        using var client = gateway.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        backend.Ready = false;
        using var unready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unready.StatusCode);
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task RateLimit_IsolatedByVerifiedUserAndRoute_ReturnsRetryAfter()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var alice = gateway.CreateClient();
        using var bob = gateway.CreateClient();
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("alice"));
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("bob"));

        for (var i = 0; i < 2; i++)
        {
            using var allowed = await alice.GetAsync(new Uri("/limited-a", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await alice.GetAsync(new Uri("/limited-a", UriKind.Relative));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        using var otherUser = await bob.GetAsync(new Uri("/limited-a", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        using var otherRoute = await alice.GetAsync(new Uri("/limited-b", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, otherRoute.StatusCode);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task UnreadyDestinations_AreRemovedFromTraffic_AndRejoinAfterRecovery()
    {
        await using var first = await GatewayBackend.StartAsync("first");
        await using var second = await GatewayBackend.StartAsync("second");
        await using var gateway = new GatewayHttpApp(first.Address, second.Address);
        using var client = gateway.CreateClient();

        using var initial = await client.GetAsync(new Uri("/probe", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        second.Ready = false;
        await EventuallyAsync(async () =>
        {
            for (var i = 0; i < 32; i++)
            {
                using var response = await client.GetAsync(new Uri("/probe", UriKind.Relative));
                if (response.StatusCode != HttpStatusCode.OK || await response.Content.ReadAsStringAsync() != "first")
                {
                    return false;
                }
            }

            return true;
        });

        first.Ready = false;
        await EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync(new Uri("/probe", UriKind.Relative));
            return response.StatusCode == HttpStatusCode.ServiceUnavailable;
        });

        second.Ready = true;
        await EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync(new Uri("/probe", UriKind.Relative));
            return response.StatusCode == HttpStatusCode.OK && await response.Content.ReadAsStringAsync() == "second";
        });
    }

    internal static string Token(string subject, bool root = false) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "nexusstack",
            audience: "nexusstack",
            claims: root ? [new Claim("sub", subject), new Claim("nexusstack:root", "true"), new Claim("nexusstack:session", "0")] : [new Claim("sub", subject)],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GatewayRouteAdminApp.SigningKey)),
                SecurityAlgorithms.HmacSha256)));

    internal static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail("HTTP behavior did not converge within 8 seconds.");
    }
}

internal sealed class GatewayHttpApp : WebApplicationFactory<GatewayHostMarker>
{
    private readonly string _directory;
    private bool _deleteConfiguration = true;
    private SessionAuthorityStub? _authority;

    public GatewayHttpApp(params string[] addresses)
    {
        _directory = Path.Combine(AppContext.BaseDirectory, $"gateway-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(RouteTablePath, JsonSerializer.Serialize(new
        {
            routes = new[]
            {
                new { routeId = "probe", clusterId = "backend", path = "/probe", requireAuthentication = false, rateLimitPolicy = (string?)null },
                new { routeId = "limited-a", clusterId = "backend", path = "/limited-a", requireAuthentication = true, rateLimitPolicy = (string?)"gateway.default" },
                new { routeId = "limited-b", clusterId = "backend", path = "/limited-b", requireAuthentication = true, rateLimitPolicy = (string?)"gateway.default" },
            },
            clusters = new[]
            {
                new
                {
                    clusterId = "backend",
                    destinations = addresses.Select((address, index) => new { name = $"node-{index}", address }),
                    healthCheck = new { path = "/health/ready", interval = "00:00:00.100", timeout = "00:00:01", failureThreshold = 2 },
                },
            },
        }));
        UseKestrel(0);
    }

    public string RouteTablePath => Path.Combine(_directory, "routes.json");
    public string SigningKey { get; init; } = GatewayRouteAdminApp.SigningKey;
    public int RateLimitPermitLimit { get; init; } = 2;
    public string? SessionAuthorityAddress { get; init; }
    public string SessionAuthorityTimeout { get; init; } = "00:00:03";
    public int SessionAuthorityConcurrency { get; init; } = 16;

    public void UseRoutes(IEnumerable<RouteDefinition> routes)
    {
        var previous = GatewayRouteTable.FromJson(File.ReadAllText(RouteTablePath)).Value;
        File.WriteAllText(RouteTablePath, GatewayRouteTable.Create(routes, previous.Clusters).Value.ToJson());
    }

    private GatewayHttpApp(string directory, bool reuseConfiguration)
    {
        _directory = directory;
        _deleteConfiguration = reuseConfiguration;
        UseKestrel(0);
    }

    public async Task<GatewayHttpApp> RestartAsync()
    {
        _deleteConfiguration = false;
        await DisposeAsync();
        return new GatewayHttpApp(_directory, reuseConfiguration: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (SessionAuthorityAddress is null)
        { _authority = SessionAuthorityStub.StartAsync(string.IsNullOrEmpty(SigningKey) ? GatewayRouteAdminApp.SigningKey : SigningKey).GetAwaiter().GetResult(); }
        builder.UseSetting("IdentitySession:BaseAddress", SessionAuthorityAddress ?? _authority!.Address);
        builder.UseSetting("IdentitySession:Timeout", SessionAuthorityTimeout);
        builder.UseSetting("IdentitySession:MaxConcurrency", SessionAuthorityConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseEnvironment("Testing");
        builder.UseSetting("OperationJournal:Storage:Provider", "Memory");
        builder.UseSetting("Gateway:RouteTablePath", RouteTablePath);
        builder.UseSetting("Gateway:RateLimit:PermitLimit", RateLimitPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Gateway:RateLimit:WindowSeconds", "60");
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Jwt:Issuer", "nexusstack");
        builder.UseSetting("Jwt:Audience", "nexusstack");
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_authority is not null) { await _authority.DisposeAsync(); }
        if (_deleteConfiguration && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class GatewayBackend(WebApplication app) : IAsyncDisposable
{
    private volatile bool _ready = true;

    public bool Ready { get => _ready; set => _ready = value; }

    public string Address => app.Urls.Single();

    public string DocumentPath { get; set; } = "/initial-contract";

    public static async Task<GatewayBackend> StartAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var backend = new GatewayBackend(app);
        app.MapGet("/health/live", () => Results.Ok());
        app.MapGet("/health/ready", () => backend.Ready ? Results.Ok() : Results.StatusCode(503));
        app.MapGet("/custom/ready", () => backend.Ready ? Results.Ok() : Results.StatusCode(503));
        app.MapGet("/echo/{**path}", (HttpContext context) => Results.Text(context.Request.Path.Value));
        app.MapGet("/openapi/v1.json", () => Results.Json(new
        {
            openapi = "3.0.1",
            paths = new Dictionary<string, object>(StringComparer.Ordinal) { [backend.DocumentPath] = new { get = new { summary = name } } },
        }));
        app.MapFallback(() => Results.Text(name));
        await app.StartAsync();
        return backend;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
