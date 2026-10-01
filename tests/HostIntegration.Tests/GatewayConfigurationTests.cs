using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class GatewayConfigurationTests
{
    [Theory]
    [InlineData("/{id:notRegistered}")]
    [InlineData("/{id:int(1)}")]
    [InlineData("/{id:regex([)}")]
    [InlineData("/{id:regex([)?}")]
    public async Task InvalidInlineConstraints_AlsoRejectStartup(string path)
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        gateway.UseRoutes([Route("invalid") with { Path = path }]);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = gateway.CreateClient();
        });
    }

    [Theory]
    [InlineData("/number/{id:int}")]
    [InlineData("/number/{id:regex(^[0-9]+$)}")]
    [InlineData("/number/{id:regex(^[0-9]+$)?}")]
    public async Task ValidInlineConstraint_IsPublished_AndActuallyConstrainsMatching(string path)
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        using var created = await client.PostAsJsonAsync(new Uri("/gateway/routes", UriKind.Relative), Route("number") with { Path = path });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        await GatewayResilienceTests.EventuallyAsync(async () =>
        {
            using var matched = await client.GetAsync(new Uri("/number/42", UriKind.Relative));
            return matched.StatusCode == HttpStatusCode.OK;
        });
        using var unmatched = await client.GetAsync(new Uri("/number/text", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
        await using var restarted = await gateway.RestartAsync();
        using var afterRestart = restarted.CreateClient();
        Assert.Equal("backend", await afterRestart.GetStringAsync(new Uri("/number/42", UriKind.Relative)));
    }

    [Fact]
    public async Task RouteIdsDifferingOnlyByCase_AreRejectedAsDuplicates()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        using var duplicate = await client.PostAsJsonAsync(new Uri("/gateway/routes", UriKind.Relative), Route("PROBE"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var restarted = await gateway.RestartAsync();
        using var afterRestart = restarted.CreateClient();
        Assert.Equal("backend", await afterRestart.GetStringAsync(new Uri("/probe", UriKind.Relative)));
    }

    [Fact]
    public async Task RouteEdits_ChangeActualForwarding_AndDeletionSurvivesRestart()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        var route = Route("edited") with
        {
            Path = "/published/{**rest}",
            Transforms = [new Dictionary<string, string>(StringComparer.Ordinal) { ["PathRemovePrefix"] = "/published" }],
        };
        using var created = await client.PostAsJsonAsync(new Uri("/gateway/routes", UriKind.Relative), route);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        await GatewayResilienceTests.EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync(new Uri("/published/echo/item", UriKind.Relative));
            return response.StatusCode == HttpStatusCode.OK && await response.Content.ReadAsStringAsync() == "/echo/item";
        });

        using var updated = await client.PutAsJsonAsync(new Uri("/gateway/routes/edited", UriKind.Relative), route with
        {
            Path = "/moved/{**rest}",
            Transforms = [new Dictionary<string, string>(StringComparer.Ordinal) { ["PathRemovePrefix"] = "/moved" }],
        });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        await GatewayResilienceTests.EventuallyAsync(async () =>
        {
            using var old = await client.GetAsync(new Uri("/published/echo/item", UriKind.Relative));
            using var moved = await client.GetAsync(new Uri("/moved/echo/item", UriKind.Relative));
            return old.StatusCode == HttpStatusCode.NotFound && moved.StatusCode == HttpStatusCode.OK &&
                await moved.Content.ReadAsStringAsync() == "/echo/item";
        });

        using var deleted = await client.DeleteAsync(new Uri("/gateway/routes/edited", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await GatewayResilienceTests.EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync(new Uri("/moved/echo/item", UriKind.Relative));
            return response.StatusCode == HttpStatusCode.NotFound;
        });
        await using var restarted = await gateway.RestartAsync();
        using var afterRestart = restarted.CreateClient();
        using var missing = await afterRestart.GetAsync(new Uri("/moved/echo/item", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PublishingRoutes_RefreshesTheAggregatedDocument()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        var before = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Contains("/initial-contract", before, StringComparison.Ordinal);
        backend.DocumentPath = "/updated-contract";

        using var created = await client.PostAsJsonAsync(new Uri("/gateway/routes", UriKind.Relative), Route("new"));
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var after = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Contains("/updated-contract", after, StringComparison.Ordinal);
        Assert.DoesNotContain("/initial-contract", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSave_KeepsTheOldRoute_AndCanBeRetriedAfterStorageRecovers()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        var backup = gateway.RouteTablePath + ".backup";
        File.Move(gateway.RouteTablePath, backup);
        Directory.CreateDirectory(gateway.RouteTablePath);
        var update = Route("probe") with { Path = "/changed" };
        try
        {
            using var failed = await client.PutAsJsonAsync(new Uri("/gateway/routes/probe", UriKind.Relative), update);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("backend", await client.GetStringAsync(new Uri("/probe", UriKind.Relative)));
            using var fetched = await client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
            var unchanged = await fetched.Content.ReadApiDataAsync();
            Assert.Equal("/probe", unchanged.GetProperty("path").GetString());
        }
        finally
        {
            Directory.Delete(gateway.RouteTablePath);
            File.Move(backup, gateway.RouteTablePath);
        }

        using var retried = await client.PutAsJsonAsync(new Uri("/gateway/routes/probe", UriKind.Relative), update);
        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        await GatewayResilienceTests.EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync(new Uri("/changed", UriKind.Relative));
            return response.StatusCode == HttpStatusCode.OK;
        });
    }

    [Fact]
    public async Task EditingTheFile_DoesNotPretendThatRunningRoutesChanged()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        var file = await File.ReadAllTextAsync(gateway.RouteTablePath);
        await File.WriteAllTextAsync(gateway.RouteTablePath, file.Replace("probe", "offline", StringComparison.Ordinal));

        var described = await client.GetStringAsync(new Uri("/gateway/routes", UriKind.Relative));
        Assert.Contains("probe", described, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", described, StringComparison.Ordinal);
        Assert.Equal("backend", await client.GetStringAsync(new Uri("/probe", UriKind.Relative)));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("bypass-policy")]
    [InlineData("path")]
    [InlineData("unknown-constraint")]
    [InlineData("constraint-arguments")]
    [InlineData("regex")]
    [InlineData("optional-regex")]
    [InlineData("transform")]
    public async Task InvalidRouteUpdate_IsRejectedAndOldRouteKeepsWorking(string invalidField)
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));
        var candidate = invalidField switch
        {
            "policy" => Route("probe") with { RateLimitPolicy = "not-registered" },
            "bypass-policy" => Route("probe") with { RateLimitPolicy = "disable" },
            "path" => Route("probe") with { Path = "/{broken" },
            "unknown-constraint" => Route("probe") with { Path = "/{id:notRegistered}" },
            "constraint-arguments" => Route("probe") with { Path = "/{id:int(1)}" },
            "regex" => Route("probe") with { Path = "/{id:regex([)}" },
            "optional-regex" => Route("probe") with { Path = "/{id:regex([)?}" },
            _ => Route("probe") with { Transforms = [new Dictionary<string, string>(StringComparer.Ordinal) { ["NotATransform"] = "x" }] },
        };

        using var rejected = await client.PutAsJsonAsync(new Uri("/gateway/routes/probe", UriKind.Relative), candidate);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var accepted = await client.GetAsync(new Uri("/gateway/routes/probe", UriKind.Relative));
        var route = (await accepted.Content.ReadApiDataAsync()).Deserialize<RouteDefinition>(JsonSerializerOptions.Web);
        Assert.NotNull(route);
        Assert.Equal("/probe", route.Path);
        Assert.Null(route.RateLimitPolicy);
        Assert.Empty(route.Transforms);
        Assert.Equal("backend", await client.GetStringAsync(new Uri("/probe", UriKind.Relative)));

        await using var restarted = await gateway.RestartAsync();
        using var afterRestart = restarted.CreateClient();
        Assert.Equal("backend", await afterRestart.GetStringAsync(new Uri("/probe", UriKind.Relative)));
    }

    [Fact]
    public async Task ConcurrentRouteCreates_AreAllPersistedAndForwarded()
    {
        await using var backend = await GatewayBackend.StartAsync("backend");
        await using var gateway = new GatewayHttpApp(backend.Address);
        using var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayResilienceTests.Token("admin", root: true));

        var writes = Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await client.PostAsJsonAsync(new Uri("/gateway/routes", UriKind.Relative), Route($"new-{index}"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        });
        await Task.WhenAll(writes);

        for (var i = 0; i < 12; i++)
        {
            using var read = await client.GetAsync(new Uri($"/gateway/routes/new-{i}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var path = $"/new-{i}";
            await GatewayResilienceTests.EventuallyAsync(async () =>
            {
                using var forwarded = await client.GetAsync(new Uri(path, UriKind.Relative));
                return forwarded.StatusCode == HttpStatusCode.OK && await forwarded.Content.ReadAsStringAsync() == "backend";
            });
        }

        await using var restarted = await gateway.RestartAsync();
        using var afterRestart = restarted.CreateClient();
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal("backend", await afterRestart.GetStringAsync(new Uri($"/new-{i}", UriKind.Relative)));
        }
    }

    private static RouteDefinition Route(string id) => new()
    {
        RouteId = id,
        ClusterId = "backend",
        Path = $"/{id}",
        RequireAuthentication = false,
    };
}
