using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SchedulingFactCapacityPolicyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task SchedulingPostgres_PolicyAndReceiptSurviveHostRecreation_WithoutChangingPlanOrDeliveryHistory()
    {
        await using var database = await databases.CreateAsync();
        var path = new Uri("/api/scheduling/audit-capacity", UriKind.Relative);
        var expansion = new FactCapacityPolicyRequest(Guid.NewGuid(), 2, 2, 268435456, 16384, "operator-adjustment");
        string receipt;
        string originalPlan;
        long id;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization;
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "scheduling-policy-root", schedulingWorkerEnabled: false))
        {
            using var client = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "scheduling-policy-root");
            authorization = client.DefaultRequestHeaders.Authorization;
            using var reduced = await client.PutAsJsonAsync(path, expansion with
            { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 1, MaxRecords = 1 });
            Assert.Equal(HttpStatusCode.OK, reduced.StatusCode);
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new
                {
                    code = "persistent-policy-plan",
                    intervalSeconds = 30,
                    firstRunInSeconds = 3600,
                    targetKind = "costing.recalculate",
                    targetId = Guid.Parse("11111111-2222-3333-4444-555555555555")
                });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            using var beforeResponse = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            originalPlan = (await beforeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText();
            using var rejected = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            using var expanded = await client.PutAsJsonAsync(path, expansion);
            Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
            var accepted = await expanded.Content.ReadApiDataAsync();
            receipt = accepted.GetRawText();
            Assert.Equal(3, accepted.GetProperty("policyRevision").ReadHttpInt64());
            using var afterResponse = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
            Assert.Equal(originalPlan, (await afterResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
        }
        await using var recreated = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var restored = recreated.CreateClient();
        restored.DefaultRequestHeaders.Authorization = authorization;
        using var replay = await restored.PutAsJsonAsync(path, expansion);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayedReceipt = await replay.Content.ReadApiDataAsync();
        Assert.Equal(receipt, replayedReceipt.GetRawText());
        using var diagnostic = await restored.GetAsync(path);
        var snapshot = await diagnostic.Content.ReadApiDataAsync();
        Assert.True(snapshot.GetProperty("isPersistent").GetBoolean());
        Assert.Equal(1, snapshot.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(2, snapshot.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
        using var unchangedResponse = await restored.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var unchanged = (await unchangedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(originalPlan, unchanged.GetRawText());
        await using var scope = recreated.Services.CreateAsyncScope();
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        Assert.Empty((await plans.ReadDecisionsAsync(id, 0, 10)).Items);
        Assert.Empty((await plans.ReadOccurrencesAsync(id, 0, 10)).Items);
        var facts = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling")
            .ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(3, facts.Count);
        Assert.Equal(2, facts.Count(fact => fact.EventName == "scheduling.fact-capacity-policy-changed.v1"));
        var eventId = replayedReceipt.GetProperty("eventId").GetGuid();
        using var payload = JsonDocument.Parse(Assert.Single(facts, fact => fact.Id == eventId).Payload);
        Assert.Equal(Assert.Single(unchanged.EnumerateArray()).GetProperty("createdBy").GetString(),
            payload.RootElement.GetProperty("actorId").GetString());
        using var paused = await restored.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        using var afterPause = await restored.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var item = Assert.Single((await afterPause.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.Equal(2, item.GetProperty("version").ReadHttpInt64());
        Assert.Equal(1, item.GetProperty("scheduleRevision").ReadHttpInt64());
        Assert.False(item.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task SchedulingMemory_FullBusinessCapacityCanExpand_WithoutChangingPlanOrDeliveryHistory()
    {
        await using var app = new PolicyApp { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new
            {
                code = "policy-plan",
                intervalSeconds = 30,
                firstRunInSeconds = 3600,
                targetKind = "costing.recalculate",
                targetId = Guid.Parse("11111111-2222-3333-4444-555555555555")
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        using var beforeResponse = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var before = await beforeResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var rejected = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("scheduling.audit_capacity_exhausted", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        using var capacityResponse = await client.GetAsync(new Uri("/api/scheduling/audit-capacity", UriKind.Relative));
        var capacity = await capacityResponse.Content.ReadApiDataAsync();
        Assert.Equal(1, capacity.GetProperty("retainedRecords").ReadHttpInt64());
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2,
            capacity.GetProperty("maxPayloadBytes").ReadHttpInt64(), capacity.GetProperty("maxRecordPayloadBytes").GetInt32(), "operator-adjustment");
        using var expanded = await client.PutAsJsonAsync(new Uri("/api/scheduling/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, expanded.StatusCode);
        var receipt = await expanded.Content.ReadApiDataAsync();
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
        using var unchangedResponse = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var unchanged = await unchangedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(before.GetProperty("data").GetRawText(), unchanged.GetProperty("data").GetRawText());
        await using var scope = app.Services.CreateAsyncScope();
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        Assert.Empty((await plans.ReadDecisionsAsync(id, 0, 10)).Items);
        Assert.Empty((await plans.ReadOccurrencesAsync(id, 0, 10)).Items);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var facts = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, facts.Count);
        var control = Assert.Single(facts, fact => fact.Id == receipt.GetProperty("eventId").GetGuid());
        Assert.Equal("scheduling.fact-capacity-policy-changed.v1", control.EventName);
        using var payload = JsonDocument.Parse(control.Payload);
        Assert.Equal(1, payload.RootElement.GetProperty("previous").GetProperty("maxRecords").GetInt64());
        Assert.Equal(2, payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64());
        using var replay = await client.PutAsJsonAsync(new Uri("/api/scheduling/audit-capacity", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        Assert.Equal(facts, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        using var paused = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        using var afterResponse = await client.GetAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var after = Assert.Single((await afterResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.Equal(2, after.GetProperty("version").ReadHttpInt64());
        Assert.False(after.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(1, after.GetProperty("scheduleRevision").ReadHttpInt64());
        using var finalResponse = await client.GetAsync(new Uri("/api/scheduling/audit-capacity", UriKind.Relative));
        var final = await finalResponse.Content.ReadApiDataAsync();
        Assert.Equal(2, final.GetProperty("retainedRecords").ReadHttpInt64());
        Assert.Equal(1, final.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
    }

    private sealed class PolicyApp : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduling:AuditDelivery:MemoryCapacity:MaxRecords"] = "1",
                ["Scheduling:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName,
                ["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword,
            }));
        }
    }
}
