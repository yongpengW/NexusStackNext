using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.PricingHost;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingHttpTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task HttpContract_RequiresOperator_AndPreservesConflictCodesAndSchemas()
    {
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString);
        var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, cost = 80m, feeRate = 0.2m };
        using var anonymous = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        app.Authenticate(root: false);
        using var forbidden = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        app.Authenticate();
        using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(accepted.Headers.Contains("X-TraceId"));
        using var conflict = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
            new { request.requestId, request.itemId, request.expectedVersion, cost = 81m, request.feeRate });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("pricing.request_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        var openApi = await app.Client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var responses = openApi.GetProperty("paths").GetProperty("/api/pricing/cost").GetProperty("post").GetProperty("responses");
        Assert.True(responses.TryGetProperty("202", out _));
        Assert.True(responses.TryGetProperty("409", out _));
        Assert.True(responses.TryGetProperty("415", out _));
    }

    [PostgresFact]
    public async Task ExplicitMigration_IsIdempotent_AndOrdinaryStartupDoesNotMigrate()
    {
        var blank = new PricingDatabaseFixture { MigrateOnInitialize = false };
        await blank.InitializeAsync();
        try
        {
            var stopped = await BusinessProcess.RunToExitAsync(BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", blank.ConnectionString));
            Assert.Equal(1, stopped.ExitCode);
            Assert.True(stopped.Output.Contains("Pricing startup failed", StringComparison.Ordinal), "Missing controlled startup diagnostic.");
            for (var count = 0; count < 2; count++)
            {
                var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", blank.ConnectionString);
                start.ArgumentList.Add("migrate-pricing");
                start.Environment["Jwt__SigningKey"] = string.Empty;
                var migrated = await BusinessProcess.RunToExitAsync(start);
                Assert.Equal(0, migrated.ExitCode);
                Assert.True(migrated.Output.Contains("Pricing migrations applied.", StringComparison.Ordinal), "Missing migration confirmation.");
            }
            await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", blank.ConnectionString);
            using var ready = await app.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally { await blank.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingMigrationConfiguration_ExitsWithControlledDiagnostic()
    {
        var start = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", string.Empty);
        start.ArgumentList.Add("migrate-pricing");
        var result = await BusinessProcess.RunToExitAsync(start);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Output.Contains("Pricing migration failed", StringComparison.Ordinal), "Missing migration diagnostic.");
    }

    [PostgresFact]
    public async Task DatabaseOutage_FailsReadiness_WhileLivenessRemainsAvailable()
    {
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString);
        try
        {
            await database.SetAvailableAsync(false);
            using var live = await app.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var ready = await app.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }
        finally { await database.SetAvailableAsync(true); }
    }

    [PostgresFact]
    public async Task GatewayForwardsAuthenticatedBusinessCommands_ToTheIndependentHost()
    {
        await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true);
        var path = Path.Combine(Path.GetTempPath(), $"nsn-pricing-routes-{Guid.NewGuid():N}.json");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Gateway"))) { root = root.Parent; }
        Assert.NotNull(root);
        var table = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "Gateway", "NexusStackNext.Gateway", "routes.pricing.json")))!;
        // 使用产品路由表，仅将测试目标替换为当前进程，避免访问开发环境服务。
        foreach (var cluster in table["clusters"]!.AsArray())
        {
            foreach (var destination in cluster!["destinations"]!.AsArray())
            {
                destination!["address"] = app.Client.BaseAddress!.ToString();
            }
        }
        await File.WriteAllTextAsync(path, table.ToJsonString());
        try
        {
            await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, path);
            var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, cost = 80m, feeRate = 0.2m };
            using var denied = await gateway.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            gateway.Authenticate();
            using var accepted = await gateway.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var task = await gateway.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{request.requestId}", UriKind.Relative));
            Assert.Equal(request.requestId, task.GetProperty("data").GetProperty("taskId").GetGuid());
        }
        finally { File.Delete(path); }
    }
    [PostgresFact]
    public async Task HttpCostCommand_AcceptsWorkThatIsQueryableAfterProcessTermination()
    {
        var requestId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString))
        {
            app.Authenticate();
            using var response = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId, itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true);
        restarted.Authenticate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var response = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{requestId}", UriKind.Relative), timeout.Token);
            if (response.GetProperty("data").GetProperty("state").GetString() == "Succeeded") { break; }
            await Task.Delay(100, timeout.Token);
        }
        var quote = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative));
        Assert.Equal(100m, quote.GetProperty("data").GetProperty("breakEvenPrice").GetDecimal());
    }

    [PostgresFact]
    public async Task KillingAWorkerDuringCompletion_RecoversAfterItsLeaseExpires()
    {
        var requestId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var pause = await database.PauseResultWriteAsync())
        {
            await using var app = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true, leaseDuration: TimeSpan.FromSeconds(5));
            app.Authenticate();
            using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId, itemId, expectedVersion = 0, cost = 80m, feeRate = 0.2m }, timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            while (true)
            {
                var task = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{requestId}", UriKind.Relative), timeout.Token);
                if (task.GetProperty("data").GetProperty("state").GetString() == "Running") { break; }
                await Task.Delay(100, timeout.Token);
            }
            var incomplete = await app.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative), timeout.Token);
            Assert.Equal(JsonValueKind.Null, incomplete.GetProperty("data").GetProperty("breakEvenPrice").ValueKind);
            // Dispose 杀掉真实进程；离开外层作用域后数据库停顿解除。
        }
        await using var restarted = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString, worker: true);
        restarted.Authenticate();
        while (true)
        {
            var task = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/tasks/{requestId}", UriKind.Relative), timeout.Token);
            if (task.GetProperty("data").GetProperty("state").GetString() == "Succeeded")
            {
                Assert.Equal(new[] { "Expired", "Succeeded" }, task.GetProperty("data").GetProperty("history").EnumerateArray().Select(x => x.GetProperty("outcome").GetString()));
                break;
            }
            await Task.Delay(100, timeout.Token);
        }
        var completed = await restarted.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/pricing/items/{itemId}", UriKind.Relative), timeout.Token);
        Assert.Equal(100m, completed.GetProperty("data").GetProperty("breakEvenPrice").GetDecimal());
    }
}
