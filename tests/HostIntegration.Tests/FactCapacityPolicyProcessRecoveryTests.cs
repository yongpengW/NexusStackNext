using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyProcessRecoveryTests
{
    [PostgresFact]
    public async Task FourPlatformPolicies_ReplayOriginalReceiptsAfterProcessCrash_WithoutChangingUsage()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var filesRoot = Path.Combine(Path.GetTempPath(), "nsn-policy-crash-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Scheduling__Worker__Enabled"] = "false",
            ["Platform__Messaging__Enabled"] = "false",
            ["Identity__Messaging__Enabled"] = "false",
            ["Files__Messaging__Enabled"] = "false",
            ["Scheduling__Messaging__Enabled"] = "false",
            ["Auditing__Messaging__Enabled"] = "false",
        };
        foreach (var context in new[] { "Platform", "Identity", "Files", "Scheduling" })
        {
            settings[$"{context}__AuditDelivery__Cleanup__Enabled"] = "false";
            settings[$"{context}__AuditDelivery__PolicyMaintenance__Enabled"] = "false";
        }
        var accepted = new Dictionary<string, (FactCapacityPolicyRequest Request, string Receipt, string Snapshot, Guid EventId)>(StringComparer.Ordinal);
        AuthenticationHeaderValue? authorization;
        await using (var producer = await PlatformHostProcess.StartAsync(database.ConnectionString, "policy-crash-root", filesRoot, settings: settings))
        {
            await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "policy-crash-root");
            authorization = producer.Client.DefaultRequestHeaders.Authorization;
            foreach (var context in new[] { "platform", "identity", "files", "scheduling" })
            {
                var path = new Uri($"/api/{context}/audit-capacity", UriKind.Relative);
                using var beforeResponse = await producer.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
                var before = await beforeResponse.Content.ReadApiDataAsync();
                var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, before.GetProperty("maxRecords").ReadHttpInt64() + 1,
                    before.GetProperty("maxPayloadBytes").ReadHttpInt64(), before.GetProperty("maxRecordPayloadBytes").GetInt32(), "operator-adjustment");
                using var response = await producer.Client.PutAsJsonAsync(path, request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var receipt = await response.Content.ReadApiDataAsync();
                Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());
                var eventId = receipt.GetProperty("eventId").GetGuid();
                Assert.NotEqual(request.RequestId, eventId);
                using var afterResponse = await producer.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
                var after = await afterResponse.Content.ReadApiDataAsync();
                Assert.Equal(before.GetProperty("retainedRecords").ReadHttpInt64(), after.GetProperty("retainedRecords").ReadHttpInt64());
                Assert.Equal(before.GetProperty("retainedPayloadBytes").ReadHttpInt64(), after.GetProperty("retainedPayloadBytes").ReadHttpInt64());
                Assert.Equal(1, after.GetProperty("controlCapacity").GetProperty("retainedRecords").ReadHttpInt64());
                accepted.Add(context, (request, receipt.GetRawText(), after.GetRawText(), eventId));
            }
            await producer.CrashAsync();
        }
        Assert.NotNull(authorization);
        Assert.Equal(4, accepted.Count);
        Assert.Equal(4, accepted.Values.Select(value => value.EventId).Distinct().Count());
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "policy-crash-root", filesRoot, settings: settings);
        // Reuse the original authenticated session so another legitimate login cannot change Identity's business usage.
        restarted.Client.DefaultRequestHeaders.Authorization = authorization;
        foreach (var (context, original) in accepted)
        {
            var path = new Uri($"/api/{context}/audit-capacity", UriKind.Relative);
            using var replay = await restarted.Client.PutAsJsonAsync(path, original.Request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(original.Receipt, (await replay.Content.ReadApiDataAsync()).GetRawText());
            using var snapshot = await restarted.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            Assert.Equal(original.Snapshot, (await snapshot.Content.ReadApiDataAsync()).GetRawText());
        }
        await using var observer = new PersistentIdentityApp(database.ConnectionString, "policy-crash-root", schedulingWorkerEnabled: false);
        await using var scope = observer.Services.CreateAsyncScope();
        foreach (var (context, original) in accepted)
        {
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
            var control = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue),
                entry => entry.EventName == context + ".fact-capacity-policy-changed.v1");
            Assert.Equal(original.EventId, control.Id);
        }
    }
}
