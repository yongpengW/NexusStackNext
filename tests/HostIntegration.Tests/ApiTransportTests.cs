using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class ApiTransportTests
{
    [Fact]
    public async Task ThroughRealGateway_EnvelopesAreNotNested_TraceMatches_AndFilesKeepTheirBytes()
    {
        await using var platform = new PlatformAppWithRootAccount();
        platform.UseKestrel(0);
        using var direct = platform.CreateClient();
        await using var gateway = new GatewayHttpApp(direct.BaseAddress!.AbsoluteUri)
        {
            SigningKey = "integration-test-signing-key-long-enough-for-hs256",
            RateLimitPermitLimit = 20,
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend" }));
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "api-contract-probe");
        using var login = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative),
            new { userName = PlatformAppWithRootAccount.RootUserName, password = PlatformAppWithRootAccount.RootPassword });
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(Assert.Single(login.Headers.GetValues("X-TraceId")), body.GetProperty("traceId").GetString());
        Assert.Equal("api-contract-probe", Assert.Single(login.Headers.GetValues("X-Correlation-Id")));
        var data = body.GetProperty("data");
        Assert.False(data.TryGetProperty("success", out _));
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var loginResponses = document.GetProperty("paths").GetProperty("/api/identity/login").GetProperty("post").GetProperty("responses");
        Assert.True(loginResponses.TryGetProperty("400", out _));
        Assert.True(loginResponses.TryGetProperty("default", out var edgeError), "聚合文档必须声明网关产生的错误。");
        var errorSchema = edgeError.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString();
        var properties = document.GetProperty("components").GetProperty("schemas").GetProperty(errorSchema!.Split('/')[^1]).GetProperty("properties");
        Assert.True(properties.TryGetProperty("errorCode", out _));
        Assert.True(properties.TryGetProperty("traceId", out _));
        HttpInt64OpenApiTests.AssertOutput(properties.GetProperty("timestamp"), nullable: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        var bytes = new byte[31 * 1024 * 1024];
        bytes[0] = 255;
        bytes[^1] = 128;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await client.PostAsync(new Uri("/api/files?name=contract.bin", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var fileId = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        using var download = await client.GetAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("contract.bin", download.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        using var deleted = await client.DeleteAsync(new Uri($"/api/files/{fileId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        using var gone = await client.GetAsync(new Uri($"/api/files/{fileId}/metadata", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        var problem = await gone.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("files.not_found", problem.GetProperty("errorCode").GetString());
        Assert.Equal(Assert.Single(gone.Headers.GetValues("X-TraceId")), problem.GetProperty("traceId").GetString());

        Assert.Equal("Healthy", await client.GetStringAsync(new Uri("/health/live", UriKind.Relative)));
        using var negotiate = await client.PostAsync(new Uri("/hubs/gateway/negotiate?negotiateVersion=1", UriKind.Relative), null);
        var negotiation = await negotiate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(negotiation.TryGetProperty("connectionToken", out _));
        Assert.False(negotiation.TryGetProperty("success", out _));
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri("/missing-route", UriKind.Relative)));
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GatewayUnavailableAndForbidden_ReturnCommonProblems()
    {
        var backend = await GatewayBackend.StartAsync("offline");
        var address = backend.Address;
        await backend.DisposeAsync();
        await using var gateway = new GatewayHttpApp(address);
        using var client = gateway.CreateClient();
        using var unavailable = await client.GetAsync(new Uri("/probe", UriKind.Relative));
        Assert.Contains(unavailable.StatusCode, new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable });
        var problem = await unavailable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(problem.GetProperty("success").GetBoolean());
        Assert.Equal((int)unavailable.StatusCode, problem.GetProperty("code").GetInt32());
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        HttpInt64OpenApiTests.AssertOutput(document.GetProperty("components").GetProperty("schemas")
            .GetProperty("EdgeProblem").GetProperty("properties").GetProperty("timestamp"), nullable: false);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("nonroot"));
        using var denied = await client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("http.403", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UnexpectedException_IsSafe_AndMalformedJsonIsBadRequest()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddApiResponseContract();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.UseApiResponseContract();
        app.MapGet("/throw", (Func<IResult>)(() => throw new InvalidOperationException("private-storage-detail")));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var failed = await client.GetAsync(new Uri("/throw", UriKind.Relative));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var raw = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-storage-detail", raw, StringComparison.Ordinal);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("http.500", body.GetProperty("errorCode").GetString());
        Assert.Equal(Assert.Single(failed.Headers.GetValues("X-TraceId")), body.GetProperty("traceId").GetString());

        await using var platform = new PlatformApp();
        using var platformClient = platform.CreateClient();
        using var invalidJson = new StringContent("{broken", Encoding.UTF8, "application/json");
        using var invalid = await platformClient.PostAsync(new Uri("/api/identity/login", UriKind.Relative), invalidJson);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("http.400", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }
}
