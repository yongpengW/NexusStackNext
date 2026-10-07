using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class OperationLoggingJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task RecoveredJournal_CanBeCleanedWhileCentralIsOffline_AndBrokerReplayRemainsIdempotent()
    {
        await using var central = await databases.CreateAsync();
        await using var source = await databases.CreateAsync("journal");
        var now = DateTimeOffset.UtcNow;
        // PostgreSQL 的时刻精度为微秒；管理请求应回传查询所得的时间，而不是本地未保存的 tick。
        var stoppedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        var message = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Source = "pricing",
            Kind = "http",
            Phase = "finished",
            Outcome = "accepted",
            StatusCode = 202,
            DurationMs = 1,
            TraceId = "recovered-delivery",
            HttpMethod = "POST",
            RouteTemplate = "/api/pricing/cost",
            OccurredAt = now,
        };
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-recovery", ClientName = prefix };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var topology = EventTopology.Create(broker.ExchangeName, [facts, operations]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using (var original = RecoverySource(source.ConnectionString, now))
            await using (var seed = original.CreateAsyncScope())
            {
                Assert.True((await seed.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(message)).IsSuccess);
                await seed.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)
                    .MarkDeadLetteredAsync(message.EventId, "stopped", stoppedAt, 0);
            }
            await using var bus = new RabbitMqEventBus(broker);
            await using var recovered = RecoverySource(source.ConnectionString, now.AddDays(2));
            await using var scope = recovered.CreateAsyncScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
            var stopped = (await maintenance.GetDeliveryAsync(message.EventId)).Value;
            Assert.True((await maintenance.RetryDeliveryAsync(
                new(Guid.NewGuid(), message.EventId, stopped.DeadLetteredAt!.Value, stopped.RetryRevision, "dependency-restored"),
                new("test-operator", "test-host"))).IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var publisher = new OutboxPublisher(outbox, bus, new FixedClock(now), new());
            Assert.Equal(1, (await publisher.PublishPendingAsync()).Delivered);
            Assert.Equal(1, await maintenance.CleanupDeliveredAsync());
            Assert.Equal(OperationJournalDeliveryErrors.NotFound, (await maintenance.GetDeliveryAsync(message.EventId)).Error);
            // 发布确认后来源副本已清理，此时中央还没有启动；消息必须仍由真实 broker 保管。
            var settings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
            settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "operation-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "operation-root-password");
            await WaitForOperationAsync(reader.Client, message.OperationId, "accepted");
            Assert.True((await scope.ServiceProvider.GetRequiredService<IOperationJournal>().AppendAsync(message)).IsSuccess);
            Assert.Equal(1, (await publisher.PublishPendingAsync()).Delivered);
            var barrier = message with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
            Assert.True((await bus.PublishAsync(OutboxEntry.From(barrier, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope())).IsSuccess);
            await WaitForOperationAsync(reader.Client, barrier.OperationId, "accepted");
            using var response = await reader.Client.GetAsync(new Uri($"/api/auditing/operations?operationId={message.OperationId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, page.GetProperty("total").ReadHttpInt64());
            Assert.Single(page.GetProperty("data").EnumerateArray());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private static ServiceProvider RecoverySource(string connectionString, DateTimeOffset now)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NexusStackNext.BuildingBlocks.Application.Time.IClock>(new FixedClock(now));
        services.AddOperationJournalPostgresStorage(connectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [AuditBrokerFact]
    public async Task PricingJournal_SurvivesProducerRestart_AndBrokerRedeliveryIsIdempotent()
    {
        await using var central = await databases.CreateAsync();
        await using var source = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(source.ConnectionString);
        var migration = BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", source.ConnectionString);
        migration.ArgumentList.Add("migrate-operation-journal");
        migration.Environment["ConnectionStrings__OperationJournal"] = source.ConnectionString;
        Assert.Equal(0, (await BusinessProcess.RunToExitAsync(migration)).ExitCode);
        var sourceSettings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = source.ConnectionString,
            ["RabbitMQ__HostName"] = string.Empty,
        };
        EventEnvelope original;
        await using (var producer = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", source.ConnectionString, settings: sourceSettings))
        {
            producer.Authenticate();
            using var accepted = await producer.Client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative),
                new { requestId = Guid.NewGuid(), itemId = Guid.NewGuid(), expectedVersion = 0, cost = 80m, feeRate = 0.2m });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var services = new ServiceCollection().AddOperationJournalPostgresStorage(source.ConnectionString);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var pending = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var entries = await pending.ReadPendingAsync(10, DateTimeOffset.UtcNow, timeout.Token);
                if (entries.Count == 2)
                {
                    original = Assert.Single(entries, entry => new SystemTextJsonIntegrationEventSerializer()
                        .Deserialize<OperationObservedV1>(entry.Payload).Phase == "finished").ToEnvelope();
                    break;
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-restart", ClientName = prefix };
        var facts = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" };
        var operations = new EventSubscription { EventName = OperationObservedV1.Name, ConsumerName = prefix + "-operations" };
        var topology = EventTopology.Create(broker.ExchangeName, [facts, operations]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, facts.ConsumerName);
            settings["Auditing__Messaging__OperationConsumerName"] = operations.ConsumerName;
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "operation-root-password", settings: settings);
            foreach (var pair in settings) { sourceSettings[pair.Key] = pair.Value; }
            await using var recovered = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", source.ConnectionString, settings: sourceSettings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "operation-root-password");
            var message = new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(original.Payload);
            var operation = await WaitForOperationAsync(reader.Client, message.OperationId, "accepted");
            Assert.Equal("pricing", operation.GetProperty("source").GetString());
            Assert.Equal("test-operator", operation.GetProperty("actorId").GetString());
            Assert.Equal(202, operation.GetProperty("statusCode").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, operation.GetProperty("startedAt").ValueKind);
            await using var bus = new RabbitMqEventBus(broker);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            var barrier = message with { EventId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
            Assert.True((await bus.PublishAsync(OutboxEntry.From(barrier, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope())).IsSuccess);
            await WaitForOperationAsync(reader.Client, barrier.OperationId, "accepted");
            using var query = await reader.Client.GetAsync(new Uri($"/api/auditing/operations?source=pricing&operationId={message.OperationId}", UriKind.Relative));
            var page = await query.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, page.GetProperty("total").ReadHttpInt64());
            Assert.Single(page.GetProperty("data").EnumerateArray());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private static async Task<JsonElement> WaitForOperationAsync(HttpClient client, Guid operationId, string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/auditing/operations?operationId={operationId}", UriKind.Relative), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var items = page.GetProperty("data").EnumerateArray().ToArray();
            if (items.Length == 1 && items[0].GetProperty("outcome").GetString() == outcome) { return items[0].Clone(); }
            await Task.Delay(100, timeout.Token);
        }
    }

    [AuditBrokerFact]
    public async Task DefaultHttpCapture_DeliversSafeStartedAndFinishedObservations()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-operations", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "auditing.operation-observed.v1", ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription,
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix + "-facts" }]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix + "-facts");
            settings["Auditing__Messaging__OperationConsumerName"] = subscription.ConsumerName;
            settings["ConnectionStrings__OperationJournal"] = database.ConnectionString;
            await using var app = await PlatformHostProcess.StartAsync(database.ConnectionString, "operation-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(app.Client, "journey-root", "operation-root-password");
            using var offsetQuery = await app.Client.GetAsync(new Uri("/api/auditing/operations?from=2026-01-01T08%3A00%3A00%2B08%3A00", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, offsetQuery.StatusCode);
            using var saved = await app.Client.PutAsJsonAsync(new Uri("/api/platform/settings/operation.probe?secret=private-query", UriKind.Relative),
                new { value = "private-operation-value", password = "private-body-password", actorId = "forged-actor" });
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            using var conflict = await app.Client.PutAsJsonAsync(new Uri("/api/platform/settings/operation.probe", UriKind.Relative),
                new { value = "private-conflict", expectedVersion = 0 });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await using (var fault = new NpgsqlConnection(database.ConnectionString))
            {
                await fault.OpenAsync();
                await using var reject = new NpgsqlCommand("ALTER TABLE platform.global_settings ADD CONSTRAINT reject_operation_test CHECK (\"Value\" <> 'private-failed-write')", fault);
                await reject.ExecuteNonQueryAsync();
            }
            using var failed = await app.Client.PutAsJsonAsync(new Uri("/api/platform/settings/operation.probe", UriKind.Relative),
                new { value = "private-failed-write", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            using var unchanged = await app.Client.GetAsync(new Uri("/api/platform/settings/operation.probe", UriKind.Relative));
            Assert.Equal("private-operation-value", (await unchanged.Content.ReadApiDataAsync()).GetProperty("value").GetString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var response = await app.Client.GetAsync(new Uri("/api/auditing/operations?limit=100", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var matches = page.GetProperty("data").EnumerateArray()
                    .Where(item => item.GetProperty("routeTemplate").GetString() == "/api/platform/settings/{key}"
                        && item.GetProperty("httpMethod").GetString() == "PUT")
                    .ToArray();
                if (matches.Length == 3 && matches.All(item => item.GetProperty("outcome").GetString() != "unconfirmed"))
                {
                    var completed = Assert.Single(matches, item => item.GetProperty("outcome").GetString() == "completed");
                    Assert.Equal(409, Assert.Single(matches, item => item.GetProperty("outcome").GetString() == "rejected").GetProperty("statusCode").GetInt32());
                    Assert.Equal(500, Assert.Single(matches, item => item.GetProperty("outcome").GetString() == "failed").GetProperty("statusCode").GetInt32());
                    Assert.Equal(204, completed.GetProperty("statusCode").GetInt32());
                    Assert.Equal("platform", completed.GetProperty("source").GetString());
                    Assert.Equal(new JwtSecurityTokenHandler().ReadJwtToken(app.Client.DefaultRequestHeaders.Authorization!.Parameter).Subject,
                        completed.GetProperty("actorId").GetString());
                    Assert.NotEqual(Guid.Empty, completed.GetProperty("operationId").GetGuid());
                    Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("startedAt").ValueKind);
                    Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("finishedAt").ValueKind);
                    Assert.DoesNotContain("private-", page.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("forged-actor", page.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("operation-root-password", page.GetRawText(), StringComparison.Ordinal);
                    return;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }
}
