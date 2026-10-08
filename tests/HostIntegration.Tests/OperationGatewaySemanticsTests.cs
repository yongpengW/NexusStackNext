using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationGatewaySemanticsTests
{
    [Theory]
    [InlineData("destination-abort", "failed", false)]
    [InlineData("route-timeout", "failed", false)]
    [InlineData("activity-timeout", "failed", false)]
    [InlineData("client-disconnect", "canceled", false)]
    [InlineData("destination-abort", "failed", true)]
    [InlineData("route-timeout", "failed", true)]
    [InlineData("activity-timeout", "failed", true)]
    [InlineData("client-disconnect", "canceled", true)]
    public async Task PartialResponse_RetainsSentStatusAndDistinguishesFailureFromClientCancellation(string termination, string outcome, bool http2)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var backend = builder.Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.MapGet("/health/ready", () => Results.Ok());
        backend.MapGet("/stream", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("part", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            if (termination == "destination-abort")
            {
                await release.Task.WaitAsync(context.RequestAborted);
                context.Abort();
            }
            else { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
        });
        await backend.StartAsync();
        await using var gateway = new GatewayHttpApp(backend.Urls.Single());
        if (http2)
        {
            gateway.UseKestrel(options => options.ConfigureEndpointDefaults(endpoint =>
                endpoint.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        }
        gateway.UseRoutes([new RouteDefinition
        {
            RouteId = "stream", ClusterId = "backend", Path = "/stream", RequireAuthentication = false,
            Timeout = TimeSpan.FromSeconds(termination == "route-timeout" ? 2 : 15),
        }]);
        if (termination == "activity-timeout")
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(gateway.RouteTablePath))!;
            document["clusters"]![0]!["requestTimeout"] = "00:00:02";
            await File.WriteAllTextAsync(gateway.RouteTablePath, document.ToJsonString());
        }
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stream");
        request.Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Add("X-Correlation-Id", "partial-response");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(request.Version, response.Version);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("part", System.Text.Encoding.UTF8.GetString(buffer));
        using var canceled = new CancellationTokenSource();
        var remaining = stream.CopyToAsync(Stream.Null, canceled.Token);
        if (termination == "client-disconnect")
        {
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remaining);
            // HTTP/2 的读取取消不等于关闭流；显式释放未完成响应才向服务端发送取消。
            await stream.DisposeAsync();
            response.Dispose();
        }
        else
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<IOException>(() => remaining.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await using var scope = gateway.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var finished = await OperationEndpointInventoryTests.WaitAsync(journal, "partial-response");
        Assert.Equal(200, finished.StatusCode);
        Assert.Equal(outcome, finished.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid\nactor")]
    public async Task AuthenticatedIdentityOutsideSafeFormat_DoesNotLoseFinishedObservation(string subject)
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token(subject));
        request.Headers.Add("X-Correlation-Id", "unsafe-actor");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = gateway.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var finished = await OperationEndpointInventoryTests.WaitAsync(journal, "unsafe-actor");
        Assert.Null(finished.ActorId);
        Assert.Equal("completed", finished.Outcome);
        Assert.Equal(200, finished.StatusCode);
    }

    [Fact]
    public async Task EdgeRejection_ProxyFailure_TimeoutAndUnknownRoute_RetainTheirActualOutcome()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddApiResponseContract();
        await using var backend = builder.Build();
        backend.UseExceptionHandler();
        backend.MapGet("/health/ready", () => Results.Ok());
        backend.MapGet("/failed", (Func<IResult>)(() => throw new InvalidOperationException("private-exception")));
        backend.MapGet("/aborted", (HttpContext context) => context.Abort());
        backend.MapGet("/slow", async (HttpContext context) => await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.MapGet("/disconnect", async (HttpContext context) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        backend.MapFallback(() => Results.NoContent());
        await backend.StartAsync();
        await using var gateway = new GatewayHttpApp(backend.Urls.Single());
        gateway.UseRoutes([
            new RouteDefinition { RouteId = "secure", ClusterId = "backend", Path = "/secure", RequireAuthentication = true },
            new RouteDefinition { RouteId = "failed", ClusterId = "backend", Path = "/failed", RequireAuthentication = false },
            new RouteDefinition { RouteId = "aborted", ClusterId = "backend", Path = "/aborted", RequireAuthentication = false },
            new RouteDefinition { RouteId = "slow", ClusterId = "backend", Path = "/slow", RequireAuthentication = false, Timeout = TimeSpan.FromMilliseconds(300) },
            new RouteDefinition { RouteId = "disconnect", ClusterId = "backend", Path = "/disconnect", RequireAuthentication = false, Timeout = TimeSpan.FromSeconds(10) },
            new RouteDefinition { RouteId = "limited", ClusterId = "backend", Path = "/limited", RequireAuthentication = false, RateLimitPolicy = "gateway.default" },
        ]);
        using var client = gateway.CreateClient();
        await using var scope = gateway.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        await CheckAsync("/secure", HttpStatusCode.Unauthorized, "rejected", "proxy");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("test-reader"));
        await CheckAsync("/gateway/routes/secure", HttpStatusCode.Forbidden, "rejected", "endpoint", HttpMethod.Put, "test-reader");
        client.DefaultRequestHeaders.Authorization = null;
        await CheckAsync("/failed", HttpStatusCode.InternalServerError, "failed", "proxy");
        await CheckAsync("/aborted", HttpStatusCode.BadGateway, "failed", "proxy");
        await CheckAsync("/slow", HttpStatusCode.GatewayTimeout, "failed", "proxy");
        for (var i = 0; i < 2; i++) { using var allowed = await client.GetAsync(new Uri("/limited", UriKind.Relative)); Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode); }
        await CheckAsync("/limited", HttpStatusCode.TooManyRequests, "rejected", "proxy");
        await CheckAsync("/private-unmatched-path", HttpStatusCode.NotFound, "rejected", "endpoint");
        using var cancel = new CancellationTokenSource();
        using var canceledRequest = new HttpRequestMessage(HttpMethod.Get, "/disconnect");
        canceledRequest.Headers.Add("X-Correlation-Id", "edge-client-disconnected");
        var pending = client.SendAsync(canceledRequest, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var canceled = await OperationEndpointInventoryTests.WaitAsync(journal, "edge-client-disconnected");
        Assert.Equal("canceled", canceled.Outcome);
        Assert.NotEqual(504, canceled.StatusCode);
        var serialized = JsonSerializer.Serialize(await OperationEndpointInventoryTests.ReadAsync(journal));
        Assert.DoesNotContain("private-", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-actor", serialized, StringComparison.Ordinal);

        async Task CheckAsync(string path, HttpStatusCode expectedStatus, string expectedOutcome, string role, HttpMethod? method = null, string? actor = null)
        {
            var correlation = "edge-" + Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path + "?token=private-query");
            request.Headers.Add("X-Correlation-Id", correlation);
            request.Headers.Add("X-User-Id", "forged-actor");
            request.Headers.Add("Cookie", "session=private-cookie");
            using var response = await client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
            var finished = await OperationEndpointInventoryTests.WaitAsync(journal, correlation);
            Assert.Equal((int)expectedStatus, finished.StatusCode);
            Assert.Equal(expectedOutcome, finished.Outcome);
            Assert.Equal(role, finished.Metadata?.ExecutionRole);
            Assert.Equal(actor, finished.ActorId);
            if (expectedStatus == HttpStatusCode.NotFound) { Assert.Null(finished.RouteTemplate); }
        }
    }
}
