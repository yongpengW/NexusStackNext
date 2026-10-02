using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Contracts;
using RabbitMQ.Client;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingDeliveryJourneyTests
{
    [AuditBrokerFact]
    public async Task ExhaustedDelivery_AfterBrokerRecovery_RequiresCurrentState_AndPreservesOccurrenceIdentity()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-scheduling", ClientName = prefix };
        var audit = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-audit" };
        var scheduled = new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = prefix + "-cost" };
        var topology = EventTopology.Create(broker.ExchangeName, [audit, scheduled]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var unavailable = AuditBusinessJourneyTests.Settings(broker with { HostName = "127.0.0.1", Port = 1 }, audit.ConsumerName);
            unavailable["Scheduling__Delivery__MaxAttempts"] = "1";
            long id;
            JsonElement failed;
            await using (var producer = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password", settings: unavailable, requireReady: false))
            {
                await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "schedule-root-password");
                using var plan = await producer.Client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
                    new { code = "retry-occurrence", intervalSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
                Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
                id = (await plan.Content.ReadApiDataAsync()).GetProperty("taskId").GetInt64();
                failed = await WaitForDeliveryAsync(producer.Client, id, "DeadLettered");
                Assert.Equal(1, failed.GetProperty("attemptCount").GetInt32());
                await producer.CrashAsync();
            }
            var settings = AuditBusinessJourneyTests.Settings(broker, audit.ConsumerName);
            settings["Scheduling__Worker__Enabled"] = "false";
            await using var recovered = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(recovered.Client, "journey-root", "schedule-root-password");
            var occurrenceId = failed.GetProperty("occurrenceId").GetGuid();
            var stoppedAt = failed.GetProperty("deadLetteredAt").GetDateTimeOffset();
            var uri = Relative($"/api/scheduling/occurrences/{occurrenceId}/retry");
            using var stale = await recovered.Client.PostAsJsonAsync(uri, new { expectedDeadLetteredAt = stoppedAt.AddSeconds(-1) });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var retried = await recovered.Client.PostAsJsonAsync(uri, new { expectedDeadLetteredAt = stoppedAt });
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
            using var repeated = await recovered.Client.PostAsJsonAsync(uri, new { expectedDeadLetteredAt = stoppedAt });
            Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
            var delivered = await WaitForDeliveryAsync(recovered.Client, id, "Delivered");
            Assert.Equal(occurrenceId, delivered.GetProperty("occurrenceId").GetGuid());
            Assert.Equal(1, delivered.GetProperty("triggerSequence").GetInt64());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task TwoContextOutboxes_AfterProducerRestart_BothDeliverThroughTheSameHost()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-scheduling", ClientName = prefix };
        var audit = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-audit" };
        var scheduled = new EventSubscription { EventName = ScheduleTriggeredV1.Name, ConsumerName = prefix + "-cost" };
        var topology = EventTopology.Create(broker.ExchangeName, [audit, scheduled]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var targetId = Guid.NewGuid();
            long id;
            Guid occurrenceId;
            await using (var producer = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password"))
            {
                await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "schedule-root-password");
                using var setting = await producer.Client.PutAsJsonAsync(Relative("/api/platform/settings/schedule.probe"), new { value = "committed" });
                Assert.Equal(HttpStatusCode.NoContent, setting.StatusCode);
                using var plan = await producer.Client.PostAsJsonAsync(Relative("/api/scheduling/tasks/"),
                    new { code = "cross-outbox", intervalSeconds = 3600, targetKind = "costing.recalculate", targetId });
                Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
                id = (await plan.Content.ReadApiDataAsync()).GetProperty("taskId").GetInt64();
                var pending = await WaitForDeliveryAsync(producer.Client, id, "Pending");
                occurrenceId = pending.GetProperty("occurrenceId").GetGuid();
                await producer.CrashAsync();
            }
            var settings = AuditBusinessJourneyTests.Settings(broker, audit.ConsumerName);
            settings["Scheduling__Worker__Enabled"] = "false";
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "schedule-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "schedule-root-password");
            await AuditBusinessJourneyTests.WaitForCountAsync(resumed.Client, 1);
            var delivered = await WaitForDeliveryAsync(resumed.Client, id, "Delivered");
            Assert.Equal(occurrenceId, delivered.GetProperty("occurrenceId").GetGuid());

            var factory = new ConnectionFactory { HostName = broker.HostName, Port = broker.Port, UserName = broker.UserName, Password = broker.Password, VirtualHost = broker.VirtualHost };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            var message = await channel.BasicGetAsync(IntegrationEventNaming.ConsumerQueue(scheduled.EventName, scheduled.ConsumerName), autoAck: true);
            Assert.NotNull(message);
            var payload = new SystemTextJsonIntegrationEventSerializer().Deserialize<ScheduleTriggeredV1>(Encoding.UTF8.GetString(message.Body.Span));
            Assert.Equal(occurrenceId, payload.EventId);
            Assert.Equal(id, payload.PlanId);
            Assert.Equal(1, payload.TriggerSequence);
            Assert.Equal(targetId, payload.TargetId);
            Assert.Equal("costing.recalculate", payload.TargetKind);
            Assert.False(string.IsNullOrEmpty(payload.CreatedBy));
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    internal static async Task<JsonElement> WaitForDeliveryAsync(HttpClient client, long id, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var observed = "Absent";
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(Relative($"/api/scheduling/tasks/{id}/occurrences"), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var items = page.GetProperty("data");
                if (items.GetArrayLength() > 0)
                {
                    var item = Assert.Single(items.EnumerateArray());
                    observed = item.GetProperty("deliveryState").GetString()!;
                    if (observed == expected) { return item.Clone(); }
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException($"计划交付预期 {expected}，最后观察到 {observed}。"); }
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}
