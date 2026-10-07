using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Scheduling.Contracts;
using Npgsql;
using RabbitMQ.Client;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditBusinessJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task CrashBetweenInboxAndAuditEntry_RollsBackReceipt_AndRedeliveryCompletes()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using (var pause = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString))
            await using (var app = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: Settings(broker, subscription.ConsumerName)))
            {
                await pause.OpenAsync();
                await using (var command = new NpgsqlCommand("""
                    SELECT pg_advisory_lock(370037);
                    CREATE FUNCTION auditing.pause_entry() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM pg_advisory_xact_lock(370037); RETURN NEW; END $$;
                    CREATE TRIGGER pause_entry BEFORE INSERT ON auditing.audit_entries
                    FOR EACH ROW EXECUTE FUNCTION auditing.pause_entry();
                    """, pause))
                {
                    await command.ExecuteNonQueryAsync();
                }
                await PlatformSettingsAccessTests.LoginAsync(app.Client, "journey-root", "audit-root-password");
                using var saved = await app.Client.PutAsJsonAsync(new Uri("/api/platform/settings/audit.interrupted", UriKind.Relative), new { value = "committed" });
                Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    // 只确认故障屏障命中；记录结果通过公开 HTTP 验收。
                    await using var blocked = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'", pause);
                    if ((long)(await blocked.ExecuteScalarAsync(timeout.Token))! > 0) { break; }
                    await Task.Delay(100, timeout.Token);
                }
                await WaitForCountAsync(app.Client, 0);
                await app.CrashAsync();
            }
            await using var recovered = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: Settings(broker, subscription.ConsumerName));
            await PlatformSettingsAccessTests.LoginAsync(recovered.Client, "journey-root", "audit-root-password");
            var result = await WaitForCountAsync(recovered.Client, 1);
            var fact = Assert.Single(result.GetProperty("data").EnumerateArray()).GetProperty("fact");
            Assert.Equal("audit.interrupted", fact.GetProperty("subjectId").GetString());
            Assert.Equal("platform.setting.created", fact.GetProperty("action").GetString());
        }
        finally { await DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task ExhaustedBrokerDelivery_IsVisibleAndCanBeRetriedAfterRestart()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var unavailable = Settings(broker with { HostName = "127.0.0.1", Port = 1 }, subscription.ConsumerName);
            unavailable["Platform__Delivery__MaxAttempts"] = "1";
            JsonElement failed;
            await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: unavailable, requireReady: false))
            {
                await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "audit-root-password");
                using var saved = await first.Client.PutAsJsonAsync(new Uri("/api/platform/settings/audit.broker", UriKind.Relative), new { value = "private-broker-value" });
                Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    using var response = await first.Client.GetAsync(new Uri("/api/platform/audit-deliveries?state=DeadLettered", UriKind.Relative), timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var entries = (await response.Content.ReadApiDataAsync()).EnumerateArray().ToArray();
                    if (entries.Length == 1) { failed = entries[0].Clone(); break; }
                    await Task.Delay(100, timeout.Token);
                }
                Assert.DoesNotContain("private-broker-value", failed.GetRawText(), StringComparison.Ordinal);
                await AssertLoggingFailureDoesNotAffectReadinessAsync(first.Client);
            }
            await using var recovered = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: Settings(broker, subscription.ConsumerName));
            await PlatformSettingsAccessTests.LoginAsync(recovered.Client, "journey-root", "audit-root-password");
            var message = failed.GetProperty("messageId").GetGuid();
            var retryUri = new Uri($"/api/platform/audit-deliveries/{message}/retry", UriKind.Relative);
            var requestId = Guid.NewGuid();
            var stoppedAt = failed.GetProperty("deadLetteredAt").GetDateTimeOffset();
            var revision = failed.GetProperty("retryRevision").GetString();
            var retry = new { requestId, expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = revision, reason = "dependency-restored" };
            using var accepted = await recovered.Client.PostAsJsonAsync(retryUri, retry);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var receipt = await accepted.Content.ReadApiDataAsync();
            using var replay = await recovered.Client.PostAsJsonAsync(retryUri, retry);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(JsonElement.DeepEquals(receipt, await replay.Content.ReadApiDataAsync()));
            using var stale = await recovered.Client.PostAsJsonAsync(retryUri,
                new { requestId = Guid.NewGuid(), expectedDeadLetteredAt = stoppedAt, expectedRetryRevision = revision, reason = "dependency-restored" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var page = await WaitForCountAsync(recovered.Client, 1);
            Assert.Equal(message, Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact").GetProperty("messageId").GetGuid());
        }
        finally { await DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task AuditStorageOutage_DoesNotBlockBusiness_AndBrokerRedeliversAcrossRestart()
    {
        await using var business = await databases.CreateAsync();
        await using var auditing = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(auditing.ConnectionString, "Auditing")).ExitCode);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix };
        var tap = new EventSubscription { EventName = subscription.EventName, ConsumerName = prefix + "-tap" };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription, tap]);
        var settings = Settings(broker, subscription.ConsumerName);
        settings["ConnectionStrings__Auditing"] = auditing.ConnectionString;
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            EventEnvelope original;
            await using (var app = await PlatformHostProcess.StartAsync(business.ConnectionString, "audit-root-password", settings: settings))
            {
                await PlatformSettingsAccessTests.LoginAsync(app.Client, "journey-root", "audit-root-password");
                await auditing.SetAvailableAsync(false);
                try
                {
                    using var committed = await app.Client.PutAsJsonAsync(new Uri("/api/platform/settings/audit.outage", UriKind.Relative), new { value = "private-during-outage" });
                    Assert.Equal(HttpStatusCode.NoContent, committed.StatusCode);
                    await AssertLoggingFailureDoesNotAffectReadinessAsync(app.Client);
                    using var live = await app.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                    original = await ReadEnvelopeAsync(broker, tap.QueueName, subscription.EventName);
                    Assert.DoesNotContain("private-during-outage", original.Payload, StringComparison.Ordinal);
                    await app.CrashAsync();
                }
                finally { await auditing.SetAvailableAsync(true); }
            }
            await using var recovered = await PlatformHostProcess.StartAsync(business.ConnectionString, "audit-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(recovered.Client, "journey-root", "audit-root-password");
            var first = await WaitForCountAsync(recovered.Client, 1);
            var entry = Assert.Single(first.GetProperty("data").EnumerateArray());
            Assert.Equal(original.MessageId, entry.GetProperty("fact").GetProperty("messageId").GetGuid());
            await using var bus = new RabbitMqEventBus(broker);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            // 后一条真实变更作为消费屏障；重复旧消息不额外创建记录。
            using var next = await recovered.Client.PutAsJsonAsync(new Uri("/api/platform/settings/audit.outage", UriKind.Relative), new { value = "recovered", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
            var final = await WaitForCountAsync(recovered.Client, 2);
            Assert.Single(final.GetProperty("data").EnumerateArray(), item => item.GetProperty("fact").GetProperty("messageId").GetGuid() == original.MessageId);
        }
        finally { await DeleteTopologyAsync(broker, topology); }
    }

    private static async Task AssertLoggingFailureDoesNotAffectReadinessAsync(HttpClient client)
    {
        using var health = new HttpClient { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        using (var ready = await health.GetAsync(new Uri("/health/ready", UriKind.Relative), budget.Token))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        }
        using var logging = await health.GetAsync(new Uri("/health/logging", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, logging.StatusCode);
        Assert.Equal("Unhealthy", await logging.Content.ReadAsStringAsync());
    }

    private static async Task<EventEnvelope> ReadEnvelopeAsync(RabbitMqOptions broker, string queue, string eventName)
    {
        var factory = new ConnectionFactory { HostName = broker.HostName, Port = broker.Port, UserName = broker.UserName, Password = broker.Password, VirtualHost = broker.VirtualHost };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var received = await channel.BasicGetAsync(queue, autoAck: true, timeout.Token);
            if (received is not null)
            {
                return new EventEnvelope
                {
                    MessageId = Guid.Parse(received.BasicProperties.MessageId!),
                    EventName = eventName,
                    OccurredAt = DateTimeOffset.FromUnixTimeSeconds(received.BasicProperties.Timestamp.UnixTime),
                    Payload = Encoding.UTF8.GetString(received.Body.Span),
                };
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    [AuditBrokerFact]
    public async Task RolledBackConflictingAndNoOpSettings_DoNotCreateCommittedFacts()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var app = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: Settings(broker, subscription.ConsumerName));
            await PlatformSettingsAccessTests.LoginAsync(app.Client, "journey-root", "audit-root-password");
            var uri = new Uri("/api/platform/settings/audit.rollback", UriKind.Relative);
            using var created = await app.Client.PutAsJsonAsync(uri, new { value = "original", description = "original" });
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
            using var noOp = await app.Client.PutAsJsonAsync(uri, new { value = "original", description = "original", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode);
            using var conflict = await app.Client.PutAsJsonAsync(uri, new { value = "conflict", expectedVersion = 0 });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("ALTER TABLE platform.global_settings ADD CONSTRAINT reject_test_value CHECK (\"Value\" <> 'reject-audit-save')", connection);
                await command.ExecuteNonQueryAsync();
            }
            using var failed = await app.Client.PutAsJsonAsync(uri, new { value = "reject-audit-save", description = "refused-description", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            using var read = await app.Client.GetAsync(uri);
            var unchanged = await read.Content.ReadApiDataAsync();
            Assert.Equal("original", unchanged.GetProperty("value").GetString());
            Assert.Equal(1, unchanged.GetProperty("version").ReadHttpInt64());
            using var changed = await app.Client.PutAsJsonAsync(uri, new { value = "changed", description = "changed", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            using var cleared = await app.Client.DeleteAsync(new Uri(uri + "?expectedVersion=3", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            var page = await WaitForCountAsync(app.Client, 3);
            var facts = page.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("fact")).OrderBy(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()).ToArray();
            Assert.Equal(new long[] { 1, 3, 4 }, facts.Select(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()));
            Assert.Equal(new[] { "platform.setting.created", "platform.setting.changed", "platform.setting.cleared" }, facts.Select(fact => fact.GetProperty("action").GetString()));
            Assert.DoesNotContain("refused-description", page.GetRawText(), StringComparison.Ordinal);
        }
        finally { await DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task CommittedSetting_SurvivesProducerRestart_AndBecomesPrivateTrustedAudit()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-audit", ClientName = prefix };
        var subscription = new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            string actor;
            await using (var producer = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password"))
            {
                await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "audit-root-password");
                actor = new JwtSecurityTokenHandler().ReadJwtToken(producer.Client.DefaultRequestHeaders.Authorization!.Parameter).Subject;
                producer.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "committed-setting-64");
                using var written = await producer.Client.PutAsJsonAsync(new Uri("/api/platform/settings/audit.probe", UriKind.Relative),
                    new { value = "private-audit-value", description = "private-audit-description", actorId = "forged-actor", source = "forged-source" });
                Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
                using var pending = await producer.Client.GetAsync(new Uri("/api/auditing/entries", UriKind.Relative));
                Assert.Empty((await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
            }
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-root-password", settings: Settings(broker, subscription.ConsumerName));
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "audit-root-password");
            var page = await WaitForCountAsync(resumed.Client, 1);
            var fact = Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact");
            Assert.Equal("platform", fact.GetProperty("source").GetString());
            Assert.Equal("platform.setting.created", fact.GetProperty("action").GetString());
            Assert.Equal(actor, fact.GetProperty("actorId").GetString());
            Assert.Equal(1, fact.GetProperty("subjectVersion").ReadHttpInt64());
            Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("traceId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("correlationId").GetString()));
            var execution = fact.GetProperty("execution");
            Assert.Equal("platform", execution.GetProperty("source").GetString());
            Assert.Equal("platform", execution.GetProperty("rootSource").GetString());
            Assert.Equal(actor, execution.GetProperty("initiatorId").GetString());
            var operationId = execution.GetProperty("operationId").GetGuid();
            Assert.NotEqual(Guid.Empty, operationId);
            Assert.Equal(operationId, execution.GetProperty("rootOperationId").GetGuid());
            Assert.Equal("committed-setting-64", fact.GetProperty("correlationId").GetString());
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/operations?source=platform&operationId={operationId}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var operations = (await response.Content.ReadApiDataAsync()).EnumerateArray().ToArray();
                if (operations.Length == 1 && operations[0].GetProperty("outcome").GetString() == "completed")
                {
                    Assert.Equal(fact.GetProperty("traceId").GetString(), operations[0].GetProperty("traceId").GetString());
                    Assert.Equal(actor, operations[0].GetProperty("actorId").GetString());
                    break;
                }
                await Task.Delay(100, budget.Token);
            }
            Assert.DoesNotContain("private-audit-value", page.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private-audit-description", page.GetRawText(), StringComparison.Ordinal);
        }
        finally { await DeleteTopologyAsync(broker, topology); }
    }

    internal static Dictionary<string, string> Settings(RabbitMqOptions broker, string consumerName) => new(StringComparer.Ordinal)
    {
        ["RabbitMQ__HostName"] = broker.HostName,
        ["RabbitMQ__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RabbitMQ__UserName"] = broker.UserName,
        ["RabbitMQ__Password"] = broker.Password,
        ["RabbitMQ__VirtualHost"] = broker.VirtualHost,
        ["RabbitMQ__ExchangeName"] = broker.ExchangeName,
        ["RabbitMQ__ClientName"] = broker.ClientName,
        ["Auditing__Messaging__ConsumerName"] = consumerName,
        ["Auditing__Messaging__OperationConsumerName"] = consumerName + "-operations",
    };

    internal static async Task<JsonElement> WaitForCountAsync(HttpClient client, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        long observed = -1;
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(new Uri("/api/auditing/entries?source=platform", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                observed = result.GetProperty("total").ReadHttpInt64();
                if (observed == count) { return result.Clone(); }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException($"审计预期 {count} 条，最后查询到 {observed} 条。"); }
    }

    internal static async Task DeleteTopologyAsync(RabbitMqOptions broker, EventTopology topology)
    {
        string[] policyTopics =
        [
            SettingFactCapacityPolicyChangedV1.Name, IdentityFactCapacityPolicyChangedV1.Name,
            FilesFactCapacityPolicyChangedV1.Name, SchedulingFactCapacityPolicyChangedV1.Name,
            CostingFactCapacityPolicyChangedV1.Name, PricingFactCapacityPolicyChangedV1.Name
        ];
        var policies = topology.Subscriptions.Where(item => item.EventName == SettingCommittedV1.Name)
            .SelectMany(item => policyTopics.Select(name => new EventSubscription { EventName = name, ConsumerName = item.ConsumerName }))
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(policies));
        var identity = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
            .Select(item => new EventSubscription { EventName = "identity.entity-committed.v1", ConsumerName = item.ConsumerName + "-identity" })
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(identity).ToArray());
        var files = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
            .Select(item => new EventSubscription { EventName = "files.stored-file-committed.v1", ConsumerName = item.ConsumerName + "-files" })
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(files).ToArray());
        var scheduling = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
            .Select(item => new EventSubscription { EventName = "scheduling.plan-committed.v1", ConsumerName = item.ConsumerName + "-scheduling" })
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(scheduling).ToArray());
        var costing = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
            .Select(item => new EventSubscription { EventName = "costing.cost-sheet-committed.v1", ConsumerName = item.ConsumerName + "-costing" })
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(costing).ToArray());
        var pricing = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
            .Select(item => new EventSubscription { EventName = "pricing.price-quote-committed.v1", ConsumerName = item.ConsumerName + "-pricing" })
            .Where(item => !topology.Subscriptions.Any(existing => existing.EventName == item.EventName && existing.ConsumerName == item.ConsumerName));
        topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(pricing).ToArray());
        if (!topology.Subscriptions.Any(item => item.EventName == "auditing.operation-observed.v1"))
        {
            var observations = topology.Subscriptions.Where(item => item.EventName == "platform.setting-committed.v1")
                .Select(item => new EventSubscription { EventName = "auditing.operation-observed.v1", ConsumerName = item.ConsumerName + "-operations" });
            topology = EventTopology.Create(topology.ExchangeName, topology.Subscriptions.Concat(observations));
        }
        var factory = new ConnectionFactory { HostName = broker.HostName, Port = broker.Port, UserName = broker.UserName, Password = broker.Password, VirtualHost = broker.VirtualHost };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in topology.AllQueueNames) { await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); }
        await channel.ExchangeDeleteAsync(topology.ExchangeName);
    }
}

internal sealed class AuditBrokerFactAttribute : FactAttribute
{
    public AuditBrokerFactAttribute()
    {
        if (RabbitMqTestBroker.TryGetOptions() is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable)))
        {
            Skip = "该审计旅程需要真实 PostgreSQL 和 RabbitMQ。";
        }
    }
}
