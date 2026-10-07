using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.Scheduling.Contracts;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyBrokerJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task AuditingHostPolicySubscriptions_AreRemovedByExistingJourneyCleanup()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-policy", ClientName = prefix };
        var subscription = new EventSubscription { EventName = SettingCommittedV1.Name, ConsumerName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName, [subscription]);
        var policySubscriptions = PolicyTopics.Select(name => new EventSubscription { EventName = name, ConsumerName = prefix }).ToArray();
        var cleanupTopology = EventTopology.Create(broker.ExchangeName, topology.Subscriptions.Concat(policySubscriptions));
        var factory = Factory(broker);
        await using var connection = await factory.CreateConnectionAsync();
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, settings: AuditBusinessJourneyTests.Settings(broker, prefix)))
            {
                // Arrange every newly composed queue through the real host, before testing its cleanup boundary.
                foreach (var policy in policySubscriptions)
                {
                    await WaitForQueueAsync(connection, policy.QueueName);
                    await WaitForQueueAsync(connection, policy.DeadLetterQueueName);
                }
            }
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
            foreach (var queue in policySubscriptions.SelectMany(item => new[] { item.QueueName, item.DeadLetterQueueName }))
            {
                await using var channel = await connection.CreateChannelAsync();
                var missing = await Assert.ThrowsAsync<OperationInterruptedException>(() => channel.QueueDeclarePassiveAsync(queue));
                Assert.NotNull(missing.ShutdownReason);
                Assert.Equal(404, missing.ShutdownReason.ReplyCode);
            }
        }
        finally
        {
            // Even a failing cleanup assertion leaves no resources: recreate only this test's topology, then remove it.
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(cleanupTopology))).IsSuccess);
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, cleanupTopology);
        }
    }

    private static readonly string[] PolicyTopics =
    [
        SettingFactCapacityPolicyChangedV1.Name, IdentityFactCapacityPolicyChangedV1.Name,
        FilesFactCapacityPolicyChangedV1.Name, SchedulingFactCapacityPolicyChangedV1.Name,
        CostingFactCapacityPolicyChangedV1.Name, PricingFactCapacityPolicyChangedV1.Name
    ];

    [AuditBrokerFact]
    public async Task PlatformPolicy_CentralOfflineAndProcessRestart_PreserveTypedEvidenceAndRejectConflictingRedelivery()
    {
        await using var business = await databases.CreateAsync();
        await using var auditing = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAuditingAsync(auditing.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-policy", ClientName = prefix };
        var subscription = new EventSubscription { EventName = SettingFactCapacityPolicyChangedV1.Name, ConsumerName = prefix };
        var tap = subscription with { ConsumerName = prefix + "-tap" };
        var topology = CentralTopology(broker, prefix, [tap]);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["ConnectionStrings__Auditing"] = auditing.ConnectionString;
        settings["Scheduling__Worker__Enabled"] = "false";
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            EventEnvelope original;
            FactCapacityPolicyRequest request;
            Guid eventId;
            var producerSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal) { ["Auditing__Messaging__Enabled"] = "false" };
            await using (var producer = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-broker-root", settings: producerSettings))
            {
                await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "policy-broker-root");
                (request, eventId) = await AdjustAsync(producer.Client, "platform");
                original = await ReadEnvelopeAsync(broker, tap);
                Assert.Equal(eventId, original.MessageId);
                Assert.NotEqual(request.RequestId, eventId);
                Assert.DoesNotContain("policy-broker-root", original.Payload, StringComparison.Ordinal);
                await using var connection = await Factory(broker).CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                var retained = await channel.QueueDeclarePassiveAsync(subscription.QueueName);
                Assert.Equal(1u, retained.MessageCount);
                await producer.CrashAsync();
            }
            await using (var central = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-broker-root", settings: settings))
            {
                await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", "policy-broker-root");
                var accepted = await WaitForPolicyCountAsync(central.Client, "platform", 1);
                AssertEvidence(Assert.Single(accepted.GetProperty("data").EnumerateArray()).GetProperty("fact"), "platform", original, request);
                await central.CrashAsync();
            }
            await using var restarted = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-broker-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "policy-broker-root");
            var restored = await WaitForPolicyCountAsync(restarted.Client, "platform", 1);
            AssertEvidence(Assert.Single(restored.GetProperty("data").EnumerateArray()).GetProperty("fact"), "platform", original, request);
            using var replay = await restarted.Client.PutAsJsonAsync(new Uri("/api/platform/audit-capacity", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var receipt = await replay.Content.ReadApiDataAsync();
            Assert.Equal(eventId, receipt.GetProperty("eventId").GetGuid());
            Assert.Equal(2, receipt.GetProperty("policyRevision").ReadHttpInt64());

            await using var bus = new RabbitMqEventBus(broker);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            Assert.True((await bus.PublishAsync(original)).IsSuccess);
            var serializer = new SystemTextJsonIntegrationEventSerializer();
            var message = serializer.Deserialize<SettingFactCapacityPolicyChangedV1>(original.Payload);
            var conflict = original with { Payload = serializer.Serialize(message with { Current = message.Current with { MaxRecords = request.MaxRecords + 1 } }) };
            Assert.True((await bus.PublishAsync(conflict)).IsSuccess);
            // Prefetch is one. This later conflicting delivery reaches DLQ after both duplicates were processed.
            var rejected = await ReadEnvelopeAsync(broker, subscription with { ConsumerName = prefix }, deadLetter: true);
            Assert.Equal(original.MessageId, rejected.MessageId);
            Assert.Equal(conflict.Payload, rejected.Payload);
            var final = await WaitForPolicyCountAsync(restarted.Client, "platform", 1);
            AssertEvidence(Assert.Single(final.GetProperty("data").EnumerateArray()).GetProperty("fact"), "platform", original, request);
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task SixSourcePolicies_ReachCentralAfterProducersExit_AndSafelyReusedRequestCreatesANewEvent()
    {
        await using var platformDatabase = await databases.CreateAsync();
        await using var costingDatabase = await databases.CreateAsync("costing");
        await using var pricingDatabase = await databases.CreateAsync("pricing");
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-policy", ClientName = prefix };
        var taps = PolicyTopics.Select(name => new EventSubscription { EventName = name, ConsumerName = prefix + "-tap" }).ToArray();
        var pricingInput = new EventSubscription { EventName = CostCalculatedV1.Name, ConsumerName = prefix + "-pricing-input" };
        var topology = CentralTopology(broker, prefix, taps.Append(pricingInput));
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["Scheduling__Worker__Enabled"] = "false";
        var expected = new Dictionary<string, (FactCapacityPolicyRequest Request, EventEnvelope Envelope)>(StringComparer.Ordinal);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var producerSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal) { ["Auditing__Messaging__Enabled"] = "false" };
            await using (var platform = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "six-policy-root", settings: producerSettings))
            {
                await PlatformSettingsAccessTests.LoginAsync(platform.Client, "journey-root", "six-policy-root");
                foreach (var source in new[] { "platform", "identity", "files", "scheduling" })
                {
                    var accepted = await AdjustAsync(platform.Client, source);
                    var envelope = await ReadEnvelopeAsync(broker, Assert.Single(taps, item => item.EventName == source + ".fact-capacity-policy-changed.v1"));
                    Assert.Equal(accepted.EventId, envelope.MessageId);
                    expected.Add(source, (accepted.Request, envelope));
                }
            }
            var costingSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                ["Costing__Messaging__Enabled"] = "true",
                ["Costing__Scheduling__Enabled"] = "false"
            };
            await using (var costing = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", costingDatabase.ConnectionString, settings: costingSettings))
            {
                costing.Authenticate();
                var accepted = await AdjustAsync(costing.Client, "costing");
                var envelope = await ReadEnvelopeAsync(broker, Assert.Single(taps, item => item.EventName == CostingFactCapacityPolicyChangedV1.Name));
                Assert.Equal(accepted.EventId, envelope.MessageId);
                expected.Add("costing", (accepted.Request, envelope));
            }
            var pricingSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                ["Pricing__Messaging__Enabled"] = "true",
                ["Pricing__Messaging__ConsumerName"] = pricingInput.ConsumerName
            };
            await using (var pricing = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", pricingDatabase.ConnectionString, settings: pricingSettings))
            {
                pricing.Authenticate();
                var accepted = await AdjustAsync(pricing.Client, "pricing");
                var envelope = await ReadEnvelopeAsync(broker, Assert.Single(taps, item => item.EventName == PricingFactCapacityPolicyChangedV1.Name));
                Assert.Equal(accepted.EventId, envelope.MessageId);
                expected.Add("pricing", (accepted.Request, envelope));
            }
            Assert.Equal(6, expected.Count);
            Assert.Equal(6, expected.Values.Select(item => item.Envelope.MessageId).Distinct().Count());
            await using (var central = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "six-policy-root", settings: settings))
            {
                await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", "six-policy-root");
                foreach (var (source, evidence) in expected)
                {
                    var found = await WaitForPolicyCountAsync(central.Client, source, 1);
                    AssertEvidence(Assert.Single(found.GetProperty("data").EnumerateArray()).GetProperty("fact"), source, evidence.Envelope, evidence.Request);
                }
                await central.CrashAsync();
            }
            await using var restarted = await PlatformHostProcess.StartAsync(platformDatabase.ConnectionString, "six-policy-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "six-policy-root");
            foreach (var (source, evidence) in expected)
            {
                var found = await WaitForPolicyCountAsync(restarted.Client, source, 1);
                AssertEvidence(Assert.Single(found.GetProperty("data").EnumerateArray()).GetProperty("fact"), source, evidence.Envelope, evidence.Request);
            }
            // Only the source owns expiry. Cross its public cleanup port, then reuse the same request under current CAS.
            await using (var sourceReader = new PersistentIdentityApp(platformDatabase.ConnectionString, "six-policy-root", schedulingWorkerEnabled: false))
            await using (var scope = sourceReader.Services.CreateAsyncScope())
            {
                var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("identity");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await cleanup.CleanupAsync(1, DateTimeOffset.UtcNow.AddDays(8), timeout.Token) != 1)
                {
                    await Task.Delay(100, timeout.Token);
                }
            }
            var identity = expected["identity"];
            var reused = identity.Request with { ExpectedPolicyRevision = 2, MaxRecords = identity.Request.MaxRecords + 1 };
            using var adjusted = await restarted.Client.PutAsJsonAsync(new Uri("/api/identity/audit-capacity", UriKind.Relative), reused);
            Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
            var newEvent = (await adjusted.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid();
            Assert.NotEqual(identity.Envelope.MessageId, newEvent);
            Assert.NotEqual(identity.Request.RequestId, newEvent);
            var history = await WaitForPolicyCountAsync(restarted.Client, "identity", 2);
            var facts = history.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("fact")).ToArray();
            Assert.All(facts, item => Assert.Equal(reused.RequestId, item.GetProperty("capacityPolicyChange").GetProperty("requestId").GetGuid()));
            Assert.Single(facts, item => item.GetProperty("messageId").GetGuid() == identity.Envelope.MessageId);
            var newer = Assert.Single(facts, item => item.GetProperty("messageId").GetGuid() == newEvent).GetProperty("capacityPolicyChange");
            Assert.Equal(3, newer.GetProperty("policyRevision").ReadHttpInt64());
            Assert.Equal(identity.Request.MaxRecords, newer.GetProperty("previous").GetProperty("maxRecords").ReadHttpInt64());
            Assert.Equal(reused.MaxRecords, newer.GetProperty("current").GetProperty("maxRecords").ReadHttpInt64());
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    internal static EventTopology CentralTopology(RabbitMqOptions broker, string consumer, IEnumerable<EventSubscription> additional)
    {
        EventSubscription[] ordinary =
        [
            new() { EventName = SettingCommittedV1.Name, ConsumerName = consumer },
            new() { EventName = IdentityEntityCommittedV1.Name, ConsumerName = consumer + "-identity" },
            new() { EventName = StoredFileCommittedV1.Name, ConsumerName = consumer + "-files" },
            new() { EventName = PlanCommittedV1.Name, ConsumerName = consumer + "-scheduling" },
            new() { EventName = CostSheetCommittedV1.Name, ConsumerName = consumer + "-costing" },
            new() { EventName = PriceQuoteCommittedV1.Name, ConsumerName = consumer + "-pricing" },
            new() { EventName = "auditing.operation-observed.v1", ConsumerName = consumer + "-operations" }
        ];
        return EventTopology.Create(broker.ExchangeName, ordinary.Concat(PolicyTopics.Select(name =>
            new EventSubscription { EventName = name, ConsumerName = consumer })).Concat(additional));
    }

    [AuditBrokerFact]
    public async Task FourMemorySourcePolicies_AlreadyPublishedEvidenceSurvivesSourceProcessExit()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-policy", ClientName = prefix };
        var taps = PolicyTopics.Take(4).Select(name => new EventSubscription { EventName = name, ConsumerName = prefix + "-tap" }).ToArray();
        var topology = CentralTopology(broker, prefix, taps);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["Scheduling__Worker__Enabled"] = "false";
        var expected = new Dictionary<string, (FactCapacityPolicyRequest Request, EventEnvelope Envelope)>(StringComparer.Ordinal);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var producerSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal)
            {
                ["DOTNET_ENVIRONMENT"] = "Testing",
                ["Auditing__Messaging__Enabled"] = "false",
                ["Identity__Storage__Provider"] = "Memory",
                ["Platform__Storage__Provider"] = "Memory",
                ["Files__Storage__Provider"] = "Memory",
                ["Scheduling__Storage__Provider"] = "Memory",
                ["OperationJournal__Storage__Provider"] = "Memory"
            };
            await using (var producer = await PlatformHostProcess.StartAsync(database.ConnectionString, "memory-policy-root", settings: producerSettings))
            {
                await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "memory-policy-root");
                foreach (var source in new[] { "platform", "identity", "files", "scheduling" })
                {
                    using var diagnosis = await producer.Client.GetAsync(new Uri($"/api/{source}/audit-capacity", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, diagnosis.StatusCode);
                    Assert.False((await diagnosis.Content.ReadApiDataAsync()).GetProperty("isPersistent").GetBoolean());
                    var accepted = await AdjustAsync(producer.Client, source);
                    var envelope = await ReadEnvelopeAsync(broker, Assert.Single(taps, item => item.EventName == source + ".fact-capacity-policy-changed.v1"));
                    Assert.Equal(accepted.EventId, envelope.MessageId);
                    expected.Add(source, (accepted.Request, envelope));
                }
                await producer.CrashAsync();
            }
            Assert.Equal(4, expected.Count);
            await using var central = await PlatformHostProcess.StartAsync(database.ConnectionString, "memory-policy-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", "memory-policy-root");
            foreach (var (source, evidence) in expected)
            {
                var found = await WaitForPolicyCountAsync(central.Client, source, 1);
                AssertEvidence(Assert.Single(found.GetProperty("data").EnumerateArray()).GetProperty("fact"), source, evidence.Envelope, evidence.Request);
            }
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [AuditBrokerFact]
    public async Task CentralPolicyEvidence_MissingAmountsAndCrashBeforeEntry_DoNotLeaveAnAcceptedInboxReceipt()
    {
        await using var business = await databases.CreateAsync();
        await using var auditing = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAuditingAsync(auditing.ConnectionString);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-policy", ClientName = prefix };
        var subscription = new EventSubscription { EventName = SettingFactCapacityPolicyChangedV1.Name, ConsumerName = prefix };
        var tap = subscription with { ConsumerName = prefix + "-tap" };
        var topology = CentralTopology(broker, prefix, [tap]);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["ConnectionStrings__Auditing"] = auditing.ConnectionString;
        settings["Scheduling__Worker__Enabled"] = "false";
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var control = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(auditing.ConnectionString) { Pooling = false }.ConnectionString);
            await control.OpenAsync();
            await using (var fault = new NpgsqlCommand("""
                CREATE FUNCTION auditing.omit_policy_amounts() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."EventName" = 'platform.fact-capacity-policy-changed.v1' THEN
                        NEW."PolicyRequestId" = NULL; NEW."PolicyRevision" = NULL; NEW."PolicyReason" = NULL;
                        NEW."PolicyPreviousMaxRecords" = NULL; NEW."PolicyPreviousMaxPayloadBytes" = NULL;
                        NEW."PolicyPreviousMaxRecordPayloadBytes" = NULL; NEW."PolicyCurrentMaxRecords" = NULL;
                        NEW."PolicyCurrentMaxPayloadBytes" = NULL; NEW."PolicyCurrentMaxRecordPayloadBytes" = NULL;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER omit_policy_amounts BEFORE INSERT ON auditing.audit_entries
                    FOR EACH ROW EXECUTE FUNCTION auditing.omit_policy_amounts();
                """, control)) { await fault.ExecuteNonQueryAsync(); }
            var producerSettings = new Dictionary<string, string>(settings, StringComparer.Ordinal) { ["Auditing__Messaging__Enabled"] = "false" };
            await using var producer = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-guard-root", settings: producerSettings);
            await PlatformSettingsAccessTests.LoginAsync(producer.Client, "journey-root", "policy-guard-root");
            var first = await AdjustAsync(producer.Client, "platform");
            (FactCapacityPolicyRequest Request, Guid EventId) second;
            var original = await ReadEnvelopeAsync(broker, tap, expectedMessageId: first.EventId);
            EventEnvelope interrupted;
            // Storage exceptions are requeued by the broker consumer, not classified as semantic dead letters.
            // Prove this specific SQL guard and its transaction rollback through the public ingestion port first.
            await using (var sourceReader = new PersistentIdentityApp(business.ConnectionString,
                auditingConnectionString: auditing.ConnectionString, schedulingWorkerEnabled: false))
            await using (var scope = sourceReader.Services.CreateAsyncScope())
            {
                var ingestion = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(subscription.EventName);
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => ingestion.HandleAsync(original));
                var databaseFailure = Assert.IsType<PostgresException>(failure.InnerException);
                Assert.Equal(PostgresErrorCodes.CheckViolation, databaseFailure.SqlState);
                Assert.Equal("auditing_fact_policy_change_valid", databaseFailure.ConstraintName);
            }
            await using (var recover = new NpgsqlCommand("DROP TRIGGER omit_policy_amounts ON auditing.audit_entries", control)) { await recover.ExecuteNonQueryAsync(); }
            await using (var central = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-guard-root", settings: settings))
            {
                await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", "policy-guard-root");
                var accepted = await WaitForPolicyCountAsync(central.Client, "platform", 1);
                AssertEvidence(Assert.Single(accepted.GetProperty("data").EnumerateArray()).GetProperty("fact"), "platform", original, first.Request);
                // The owned database barrier pauses the entry write after inbox acquisition; HTTP remains the verdict.
                await using (var pause = new NpgsqlCommand("""
                    SELECT pg_advisory_lock(810037);
                    CREATE FUNCTION auditing.pause_policy_entry() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW."EventName" = 'platform.fact-capacity-policy-changed.v1' THEN
                            PERFORM pg_advisory_xact_lock(810037);
                        END IF;
                        RETURN NEW;
                    END $$;
                    CREATE TRIGGER pause_policy_entry BEFORE INSERT ON auditing.audit_entries
                        FOR EACH ROW EXECUTE FUNCTION auditing.pause_policy_entry();
                    """, control)) { await pause.ExecuteNonQueryAsync(); }
                second = await AdjustAsync(producer.Client, "platform");
                interrupted = await ReadEnvelopeAsync(broker, tap, expectedMessageId: second.EventId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    await using var blocked = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'", control);
                    if ((long)(await blocked.ExecuteScalarAsync(timeout.Token))! > 0) { break; }
                    await Task.Delay(100, timeout.Token);
                }
                _ = await WaitForPolicyCountAsync(central.Client, "platform", 1);
                await central.CrashAsync();
            }
            await using (var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(810037)", control)) { Assert.True((bool)(await unlock.ExecuteScalarAsync())!); }
            await producer.CrashAsync();
            await using var restarted = await PlatformHostProcess.StartAsync(business.ConnectionString, "policy-guard-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "policy-guard-root");
            var final = await WaitForPolicyCountAsync(restarted.Client, "platform", 2);
            var facts = final.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("fact")).ToArray();
            AssertEvidence(Assert.Single(facts, item => item.GetProperty("messageId").GetGuid() == original.MessageId), "platform", original, first.Request);
            AssertEvidence(Assert.Single(facts, item => item.GetProperty("messageId").GetGuid() == interrupted.MessageId), "platform", interrupted, second.Request);
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private static async Task MigrateAuditingAsync(string connection)
    {
        await using var context = new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNexusStackPostgres(connection, AuditingDbContext.SchemaName).Options);
        await context.Database.MigrateAsync();
    }

    internal static async Task<(FactCapacityPolicyRequest Request, Guid EventId)> AdjustAsync(HttpClient client, string source)
    {
        var path = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initialResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), initial.GetProperty("policyRevision").ReadHttpInt64(),
            initial.GetProperty("maxRecords").ReadHttpInt64() + 1, initial.GetProperty("maxPayloadBytes").ReadHttpInt64(),
            initial.GetProperty("maxRecordPayloadBytes").GetInt32(), "operator-adjustment");
        using var adjusted = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        return (request, (await adjusted.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid());
    }

    internal static async Task<EventEnvelope> ReadEnvelopeAsync(RabbitMqOptions broker, EventSubscription subscription, bool deadLetter = false, Guid? expectedMessageId = null)
    {
        await using var connection = await Factory(broker).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var received = await channel.BasicGetAsync(deadLetter ? subscription.DeadLetterQueueName : subscription.QueueName, autoAck: true, timeout.Token);
            if (received is not null)
            {
                Assert.Equal(subscription.EventName, received.BasicProperties.Type);
                if (expectedMessageId is not null && Guid.Parse(received.BasicProperties.MessageId!) != expectedMessageId) { continue; }
                return new EventEnvelope
                {
                    MessageId = Guid.Parse(received.BasicProperties.MessageId!),
                    EventName = received.BasicProperties.Type!,
                    OccurredAt = DateTimeOffset.FromUnixTimeSeconds(received.BasicProperties.Timestamp.UnixTime),
                    Payload = Encoding.UTF8.GetString(received.Body.Span)
                };
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    internal static async Task<JsonElement> WaitForPolicyCountAsync(HttpClient client, string source, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        long observed = -1;
        try
        {
            while (true)
            {
                using var response = await client.GetAsync(new Uri($"/api/auditing/entries?source={source}&subjectType=fact-capacity-policy&subjectId={source}", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                observed = page.GetProperty("total").ReadHttpInt64();
                if (observed == count) { return page.Clone(); }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException($"Policy investigation for {source}: expected {count}, observed {observed}."); }
    }

    internal static void AssertEvidence(JsonElement fact, string source, EventEnvelope original, FactCapacityPolicyRequest request)
    {
        using var payload = JsonDocument.Parse(original.Payload);
        var message = payload.RootElement;
        Assert.Equal(original.MessageId, fact.GetProperty("messageId").GetGuid());
        Assert.Equal(original.EventName, fact.GetProperty("eventName").GetString());
        Assert.Equal(source, fact.GetProperty("source").GetString());
        Assert.Equal(source + ".fact-capacity-policy.changed", fact.GetProperty("action").GetString());
        Assert.Equal(source, fact.GetProperty("subjectId").GetString());
        Assert.Equal(message.GetProperty("actorId").GetString(), fact.GetProperty("actorId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("actorId").GetString()));
        // PostgreSQL timestamps retain microseconds; the original payload and fingerprint retain all ticks.
        Assert.InRange((message.GetProperty("occurredAt").GetDateTimeOffset() - fact.GetProperty("occurredAt").GetDateTimeOffset()).Ticks, 0, 9);
        Assert.Equal(message.GetProperty("traceId").GetString(), fact.GetProperty("traceId").GetString());
        Assert.Equal(message.GetProperty("correlationId").GetString(), fact.GetProperty("correlationId").GetString());
        Assert.Equal(message.GetProperty("execution").GetProperty("operationId").GetGuid(), fact.GetProperty("execution").GetProperty("operationId").GetGuid());
        var evidence = fact.GetProperty("capacityPolicyChange");
        Assert.Equal(request.RequestId, evidence.GetProperty("requestId").GetGuid());
        Assert.Equal(request.ExpectedPolicyRevision + 1, evidence.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal("operator-adjustment", evidence.GetProperty("reason").GetString());
        Assert.Equal(request.MaxRecords - 1, evidence.GetProperty("previous").GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(request.MaxRecords, evidence.GetProperty("current").GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(request.MaxPayloadBytes, evidence.GetProperty("current").GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(request.MaxRecordPayloadBytes, evidence.GetProperty("current").GetProperty("maxRecordPayloadBytes").GetInt32());
    }

    private static ConnectionFactory Factory(RabbitMqOptions broker) => new()
    {
        HostName = broker.HostName,
        Port = broker.Port,
        UserName = broker.UserName,
        Password = broker.Password,
        VirtualHost = broker.VirtualHost
    };

    private static async Task WaitForQueueAsync(IConnection connection, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
            try { _ = await channel.QueueDeclarePassiveAsync(queue, timeout.Token); return; }
            catch (OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode == 404) { }
            await Task.Delay(100, timeout.Token);
        }
    }
}
