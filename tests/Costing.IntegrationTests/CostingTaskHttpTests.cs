using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusStackNext.CostingHost;
using NexusStackNext.Gateway;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingTaskHttpTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task DelayBinding_RejectsFractionalAndOutOfRangeRequestsWithoutAcceptingInputs()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        foreach (var delay in new[] { -1m, 2_592_001m, 0.5m })
        {
            var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m, delaySeconds = delay };
            using var rejected = await host.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var task = await host.Client.GetAsync(new Uri($"/api/costing/tasks/{request.requestId}", UriKind.Relative));
            using var item = await host.Client.GetAsync(new Uri($"/api/costing/items/{request.itemId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, task.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);
        }
    }

    [PostgresFact]
    public async Task InvalidLeaseBudget_PreventsOrdinaryHostStartup()
    {
        foreach (var budget in new[] { "00:00:01", "1.00:00:00.000001", "00:30:00.0000001" })
        {
            var start = BusinessProcess.StartInfo(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
            start.Environment["Costing__Tasks__MaxLeaseDuration"] = budget;
            Assert.Equal(1, (await BusinessProcess.RunToExitAsync(start)).ExitCode);
        }
    }

    [PostgresFact]
    public async Task Gateway_DelaysListsAndCancelsWork_AndPreservesCancellationAfterRestart()
    {
        var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m, delaySeconds = 120 };
        DateTimeOffset? acceptedAt = null;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, worker: true);
            var routePath = Path.Combine(Path.GetTempPath(), $"nsn-costing-management-routes-{Guid.NewGuid():N}.json");
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Gateway"))) { root = root.Parent; }
            Assert.NotNull(root);
            var routes = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "Gateway", "NexusStackNext.Gateway", "routes.business.json")))!;
            foreach (var cluster in routes["clusters"]!.AsArray())
            {
                foreach (var destination in cluster!["destinations"]!.AsArray()) { destination!["address"] = host.Client.BaseAddress!.ToString(); }
            }
            await File.WriteAllTextAsync(routePath, routes.ToJsonString());
            try
            {
                await using var gateway = await BusinessProcess.StartGatewayAsync(typeof(GatewayHostMarker).Assembly.Location, routePath);
                var listPath = new Uri($"/api/costing/tasks?itemId={request.itemId}&limit=1", UriKind.Relative);
                using var anonymous = await gateway.Client.GetAsync(listPath);
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                gateway.Authenticate(root: false);
                using var forbidden = await gateway.Client.GetAsync(listPath);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                using var deniedCancel = await gateway.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{request.requestId}/cancel", UriKind.Relative), new { expectedEpoch = "0" });
                Assert.Equal(HttpStatusCode.Forbidden, deniedCancel.StatusCode);
                gateway.Authenticate();
                if (cycle == 0)
                {
                    using var accepted = await gateway.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
                    Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                    var task = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
                    acceptedAt = task.GetProperty("createdAt").GetDateTimeOffset();
                    Assert.Equal(acceptedAt.Value.AddSeconds(120), task.GetProperty("availableAt").GetDateTimeOffset());
                }
                using var listed = await gateway.Client.GetAsync(listPath);
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                var page = (await listed.Content.ReadFromJsonAsync<JsonElement>());
                Assert.Equal(JsonValueKind.String, page.GetProperty("total").ValueKind);
                Assert.Equal(1, page.GetProperty("total").ReadHttpInt64());
                var item = Assert.Single(page.GetProperty("data").EnumerateArray());
                Assert.Equal(request.requestId, item.GetProperty("taskId").GetGuid());
                Assert.Equal(cycle == 0 ? "Pending" : "Cancelled", item.GetProperty("state").GetString());
                Assert.Equal("0", item.GetProperty("epoch").GetString());
                Assert.Equal(acceptedAt, item.GetProperty("createdAt").GetDateTimeOffset());
                foreach (var privateField in new[] { "purchaseCost", "freightCost", "history" }) { Assert.False(item.TryGetProperty(privateField, out _)); }

                using var missingCondition = await gateway.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{request.requestId}/cancel", UriKind.Relative), new { });
                Assert.Equal(HttpStatusCode.BadRequest, missingCondition.StatusCode);
                using var cancelled = await gateway.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{request.requestId}/cancel", UriKind.Relative), new { expectedEpoch = "0" });
                Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
                var cancellation = (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
                Assert.Equal("Cancelled", cancellation.GetProperty("state").GetString());
                Assert.Empty(cancellation.GetProperty("history").EnumerateArray());
                using var conflict = await gateway.Client.PostAsJsonAsync(new Uri($"/api/costing/tasks/{request.requestId}/cancel", UriKind.Relative), new { expectedEpoch = "1" });
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Equal("costing.cancel_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
                using var invalid = await gateway.Client.GetAsync(new Uri("/api/costing/tasks?page=2147483647&limit=200", UriKind.Relative));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                using var sheet = await gateway.Client.GetAsync(new Uri($"/api/costing/items/{request.itemId}", UriKind.Relative));
                Assert.Equal(JsonValueKind.Null, (await sheet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("unitCost").ValueKind);
            }
            finally { File.Delete(routePath); }
        }
    }
}
