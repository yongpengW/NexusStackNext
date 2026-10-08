using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SessionAuthorityHttpTests
{
    [Fact]
    public async Task UntrustedDecisions_DoNotMutateRoutes_AndLaterRecoveryDoesNotReuseAnEarlierAllow()
    {
        var mode = "valid";
        var calls = 0;
        await using var authority = await SessionAuthorityStub.StartAsync(GatewayRouteAdminApp.SigningKey, async http =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("/api/identity/session/v1", http.Request.Path);
            Assert.Equal(string.Empty, http.Request.QueryString.Value ?? string.Empty);
            if (mode == "timeout") { await Task.Delay(TimeSpan.FromSeconds(10), http.RequestAborted); }
            if (mode == "truncated")
            {
                http.Response.ContentType = "application/json";
                http.Response.ContentLength = 128;
                await http.Response.WriteAsync("{", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                Assert.True(http.Response.HasStarted);
                await Task.Delay(100, http.RequestAborted);
                http.Abort();
                return Results.Empty;
            }
            if (mode == "malformed") { return Results.Text("not-json", "application/json"); }
            if (mode == "oversized") { return Results.Text(new string('x', 4097), "application/json"); }
            if (mode == "unavailable") { return Results.StatusCode(503); }
            if (mode == "revoked") { return Results.Unauthorized(); }
            if (mode == "missing-root") { return Results.Json(new { success = true, code = 200, data = new { contractVersion = 1, subject = "admin", sessionVersion = "0" } }); }
            return Results.Json(new
            {
                success = true,
                code = 200,
                data = new { contractVersion = mode == "wrong-contract" ? 2 : 1, subject = mode == "wrong-subject" ? "other" : "admin", sessionVersion = mode == "wrong-version" ? "1" : "0", isRoot = mode != "non-root" },
            });
        });
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address)
        { SessionAuthorityAddress = authority.Address, SessionAuthorityTimeout = "00:00:00.500" };
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", true));
        using (var initial = await client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative))) { Assert.Equal(HttpStatusCode.OK, initial.StatusCode); }
        foreach (var fault in new[] { "unavailable", "timeout", "truncated", "malformed", "oversized", "missing-root", "wrong-contract", "wrong-subject", "wrong-version", "revoked", "non-root" })
        {
            mode = fault;
            var started = Stopwatch.StartNew();
            using var rejected = await client.PostAsJsonAsync(new Uri("/gateway/routes/", UriKind.Relative), new { routeId = "must-not-exist", clusterId = "backend", path = "/must-not-exist" });
            Assert.Equal(fault == "revoked" ? HttpStatusCode.Unauthorized : fault == "non-root" ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
            if (rejected.StatusCode == HttpStatusCode.ServiceUnavailable)
            { Assert.Contains("identity.session.unavailable", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal); }
            mode = "valid";
            using var absent = await client.GetAsync(new Uri("/gateway/routes/must-not-exist", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        Assert.Equal(23, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task RedirectResponse_DoesNotForwardBearerToAnotherTarget()
    {
        var redirected = 0;
        await using var target = await SessionAuthorityStub.StartAsync(GatewayRouteAdminApp.SigningKey, _ =>
        {
            Interlocked.Increment(ref redirected);
            return Task.FromResult<IResult>(Results.Ok());
        });
        await using var authority = await SessionAuthorityStub.StartAsync(GatewayRouteAdminApp.SigningKey,
            _ => Task.FromResult<IResult>(Results.Redirect(target.Address + "/api/identity/session/v1")));
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address) { SessionAuthorityAddress = authority.Address };
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", true));
        using var denied = await client.DeleteAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.Equal(0, Volatile.Read(ref redirected));
        client.DefaultRequestHeaders.Authorization = null;
        using var listed = await client.GetAsync(new Uri("/gateway/routes", UriKind.Relative));
        Assert.Contains("probe", await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaturatedAuthorityCalls_AreRejectedBeforeRouteMutation_AndTheInFlightAuthorizationMayFinish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var authority = await SessionAuthorityStub.StartAsync(GatewayRouteAdminApp.SigningKey, async http =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(http.RequestAborted);
            return Results.Json(new { success = true, code = 200, data = new { contractVersion = 1, subject = "admin", sessionVersion = "0", isRoot = true } });
        });
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address) { SessionAuthorityAddress = authority.Address, SessionAuthorityConcurrency = 1 };
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", true));
        var inFlight = client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var saturated = await client.DeleteAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, saturated.StatusCode);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
        using var completed = await inFlight;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var preserved = await client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
    }
}
