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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("verified-operator"));
        await CheckAsync("/gateway/routes/secure", HttpStatusCode.Forbidden, "rejected", "endpoint", HttpMethod.Put, "verified-operator");
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
