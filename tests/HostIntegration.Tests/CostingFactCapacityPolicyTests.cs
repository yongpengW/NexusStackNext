using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CostingFactCapacityPolicyTests
{
    [PostgresFact]
    public async Task CostingPolicyExpansion_SurvivesProcessRestart_AndPreservesCostAndAcceptedTask()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var arrange = new NpgsqlCommand("UPDATE costing.fact_capacity SET \"MaxRecords\" = 1", connection);
            Assert.Equal(1, await arrange.ExecuteNonQueryAsync());
        }
        var assembly = typeof(CostingHostMarker).Assembly.Location;
        var path = new Uri("/api/costing/audit-capacity", UriKind.Relative);
        var itemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var first = new { requestId = taskId, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m };
        var second = new { requestId = Guid.NewGuid(), itemId, expectedVersion = "1", purchaseCost = 90m, freightCost = 10m };
        var policy = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 268435456, 16384, "operator-adjustment");
        string receipt;
        string originalCost;
        string originalTask;
        await using (var host = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString))
        {
            host.Authenticate();
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), first);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var cost = await host.Client.GetAsync(new Uri($"/api/costing/items/{itemId}", UriKind.Relative));
            originalCost = (await cost.Content.ReadApiDataAsync()).GetRawText();
            using var task = await host.Client.GetAsync(new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative));
            originalTask = (await task.Content.ReadApiDataAsync()).GetRawText();
            using var refused = await host.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), second);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("costing.audit_capacity_exhausted", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            using var expanded = await host.Client.PutAsJsonAsync(path, policy);
            Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
            var acceptedPolicy = await expanded.Content.ReadApiDataAsync();
            receipt = acceptedPolicy.GetRawText();
            Assert.Equal(2, acceptedPolicy.GetProperty("policyRevision").ReadHttpInt64());
        }
        await using var restarted = await BusinessProcess.StartAsync(assembly, "Costing", database.ConnectionString);
        restarted.Authenticate();
        using var replay = await restarted.Client.PutAsJsonAsync(path, policy);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadApiDataAsync();
        Assert.Equal(receipt, replayed.GetRawText());
        using var unchangedCost = await restarted.Client.GetAsync(new Uri($"/api/costing/items/{itemId}", UriKind.Relative));
        Assert.Equal(originalCost, (await unchangedCost.Content.ReadApiDataAsync()).GetRawText());
        using var unchangedTask = await restarted.Client.GetAsync(new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative));
        Assert.Equal(originalTask, (await unchangedTask.Content.ReadApiDataAsync()).GetRawText());
        using var rejectedTask = await restarted.Client.GetAsync(new Uri($"/api/costing/tasks/{second.requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, rejectedTask.StatusCode);
        await using var reader = TaskOperationTests.CreateCostingApp(database.ConnectionString, null);
        await using var scope = reader.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        var control = Assert.Single(facts, fact => fact.Id == replayed.GetProperty("eventId").GetGuid());
        Assert.Equal("costing.fact-capacity-policy-changed.v1", control.EventName);
        using var payload = JsonDocument.Parse(control.Payload);
        Assert.Equal("test-operator", payload.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("previous").GetProperty("maxRecords").GetInt64());
        Assert.Equal(2, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        using var retry = await restarted.Client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), second);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var afterResponse = await restarted.Client.GetAsync(path);
        var after = await afterResponse.Content.ReadApiDataAsync();
        Assert.True(after.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(2, after.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
    }
}
