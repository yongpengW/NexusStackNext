using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingHostTests
{
    [PostgresFact]
    public async Task MigrationIsExplicitAndRepeatable_AndHttpRequiresAnOperator()
    {
        var database = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await database.InitializeAsync();
        try
        {
            var assembly = typeof(CostingHostMarker).Assembly.Location;
            var stopped = await BusinessProcess.RunToExitAsync(BusinessProcess.StartInfo(assembly, "Costing", database.ConnectionString));
            Assert.Equal(1, stopped.ExitCode);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var start = BusinessProcess.StartInfo(assembly, "Costing", database.ConnectionString);
                start.ArgumentList.Add("migrate-costing");
                start.Environment.Remove("Jwt__SigningKey");
                var migrated = await BusinessProcess.RunToExitAsync(start);
                Assert.Equal(0, migrated.ExitCode);
            }
            await using var app = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString);
            var request = new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, purchaseCost = 80m, freightCost = 20m };
            using var anonymous = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            app.Authenticate(root: false);
            using var forbidden = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            app.Authenticate();
            using var accepted = await app.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.True(accepted.Headers.Contains("X-TraceId"));
            var document = await app.Client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
            var responses = document.GetProperty("paths").GetProperty("/api/costing/tasks/{taskId}/delivery/retry").GetProperty("post").GetProperty("responses");
            Assert.True(responses.TryGetProperty("202", out _));
            Assert.True(responses.TryGetProperty("409", out _));
        }
        finally { await database.DisposeAsync(); }
    }
}
