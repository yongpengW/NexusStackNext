using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.TestSupport;
using Xunit.Abstractions;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactRecoveryBrokerJourneyTests(JourneyDatabaseTemplates databases, ITestOutputHelper output)
{
    [AuditBrokerFact]
    public Task Platform_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("platform");

    [AuditBrokerFact]
    public Task Identity_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("identity");

    [AuditBrokerFact]
    public Task Files_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("files");

    [AuditBrokerFact]
    public Task Scheduling_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("scheduling");

    [AuditBrokerFact]
    public Task Costing_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("costing");

    [AuditBrokerFact]
    public Task Pricing_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts()
        => VerifyAsync("pricing");

    [AuditBrokerFact]
    public async Task FourMemorySources_RecoverBothFactKinds_AndCentralEvidenceSurvivesProducerDisposalAndCentralRestart()
    {
        using var timings = new JourneyPhaseTimings(output);
        timings.MoveTo(JourneyPhase.DatabasePreparation);
        await using var database = await databases.CreateAsync();
        timings.MoveTo(JourneyPhase.BrokerTopology);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-memory-recovery", ClientName = prefix };
        string[] sources = ["platform", "identity", "files", "scheduling"];
        var taps = sources.SelectMany(source => new[]
        {
            new EventSubscription { EventName = source + ".fact-capacity-policy-changed.v1", ConsumerName = prefix + "-tap" },
            new EventSubscription { EventName = OrdinaryEventName(source), ConsumerName = prefix + "-ordinary-tap" },
        }).ToArray();
        var topology = FactCapacityPolicyBrokerJourneyTests.CentralTopology(broker, prefix, taps);
        var storage = Directory.CreateTempSubdirectory("nsn-recovery-broker-");
        var evidence = new Dictionary<string, (FactCapacityPolicyRequest Policy, EventEnvelope PolicyEnvelope,
            EventEnvelope OrdinaryEnvelope, Guid RequestId)>(StringComparer.Ordinal);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            timings.MoveTo(JourneyPhase.FixtureStartup);
            await using (var factory = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false })
            await using (var producer = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Files:StorageRoot"] = storage.FullName,
                    ["RabbitMQ:HostName"] = string.Empty,
                    ["AgileConfig:AppId"] = string.Empty,
                }))))
            {
                using var client = producer.CreateClient();
                timings.MoveTo(JourneyPhase.SourceAssertions);
                await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
                await using var scope = producer.Services.CreateAsyncScope();
                await using var offline = new RabbitMqEventBus(broker with { HostName = "127.0.0.1", Port = 1 });
                await using var bus = new RabbitMqEventBus(broker);
                foreach (var source in sources)
                {
                    var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
                    var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
                    Assert.False((await delivery.ReadRecoveryCapacityAsync()).Value.IsPersistent);
                    var adjusted = await FactCapacityPolicyBrokerJourneyTests.AdjustAsync(client, source);
                    var policy = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == adjusted.EventId);
                    var previous = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Select(entry => entry.Id).ToHashSet();
                    await CommitOrdinaryAsync(client, source);
                    var ordinary = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue))
                        .Where(entry => !previous.Contains(entry.Id) && entry.EventName == OrdinaryEventName(source))
                        .OrderBy(entry => entry.OccurredAt).First();
                    var stoppedAt = DateTimeOffset.UtcNow;
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    Assert.True((await new OutboxPublisher(outbox, offline, new FixedClock(stoppedAt), new() { MaxAttempts = 1 })
                        .PublishPendingAsync(budget.Token)).DeadLettered > 0);
                    Assert.Equal("DeadLettered", (await delivery.GetAsync(policy.Id)).Value.State);
                    Assert.Equal("DeadLettered", (await delivery.GetAsync(ordinary.Id)).Value.State);
                    var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), policy.Id, stoppedAt, 0, "dependency-restored");
                    var receipt = await RecoverAsync(client, source, request);
                    var ordinaryRequest = request with { RequestId = Guid.NewGuid(), MessageId = ordinary.Id };
                    var ordinaryReceipt = await RecoverAsync(client, source, ordinaryRequest);
                    Assert.True(JsonElement.DeepEquals(receipt, await RecoverAsync(client, source, request)));
                    Assert.True(JsonElement.DeepEquals(ordinaryReceipt, await RecoverAsync(client, source, ordinaryRequest)));
                    foreach (var original in new[] { policy, ordinary })
                    {
                        var pending = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == original.Id);
                        Assert.Equal(original.Payload, pending.Payload);
                        Assert.Equal(original.EventName, pending.EventName);
                        Assert.Equal(original.OccurredAt, pending.OccurredAt);
                        Assert.Equal(1, pending.RetryRevision);
                    }
                    Assert.Equal(2, (await new OutboxPublisher(outbox, bus, new FixedClock(stoppedAt.AddSeconds(1)), new())
                        .PublishPendingAsync(budget.Token)).Delivered);
                    foreach (var original in new[] { policy, ordinary })
                    {
                        Assert.Equal("Delivered", (await delivery.GetAsync(original.Id)).Value.State);
                        Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "late-after-confirmation", stoppedAt, 1));
                    }
                    var policyEnvelope = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker,
                        Assert.Single(taps, tap => tap.EventName == policy.EventName), expectedMessageId: policy.Id);
                    var ordinaryEnvelope = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker,
                        Assert.Single(taps, tap => tap.EventName == ordinary.EventName), expectedMessageId: ordinary.Id);
                    Assert.Equal(policy.Payload, policyEnvelope.Payload);
                    Assert.Equal(ordinary.Payload, ordinaryEnvelope.Payload);
                    evidence.Add(source, (adjusted.Request, policyEnvelope, ordinaryEnvelope, request.RequestId));
                }
                timings.MoveTo(JourneyPhase.SourceCleanup);
            }
            // Memory keeps no durable source receipt; broker-confirmed evidence belongs to the separate central store.
            await using (var fresh = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false })
            {
                await using var scope = fresh.Services.CreateAsyncScope();
                foreach (var (source, expected) in evidence)
                {
                    Assert.Equal(source + ".delivery_recovery.not_found",
                        (await FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source).GetRecoveryAsync(expected.RequestId)).Error.Code);
                }
            }
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
            settings["Scheduling__Worker__Enabled"] = "false";
            var originals = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            timings.MoveTo(JourneyPhase.CentralStartup);
            await using (var central = await PlatformHostProcess.StartAsync(database.ConnectionString, RootPassword, settings: settings))
            {
                timings.MoveTo(JourneyPhase.CentralAssertions);
                await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", RootPassword);
                foreach (var (source, expected) in evidence)
                {
                    var page = await FactCapacityPolicyBrokerJourneyTests.WaitForPolicyCountAsync(central.Client, source, 1);
                    FactCapacityPolicyBrokerJourneyTests.AssertEvidence(Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact"),
                        source, expected.PolicyEnvelope, expected.Policy);
                    originals.Add(source, await WaitForOrdinaryAsync(central.Client, source, expected.OrdinaryEnvelope));
                }
                timings.MoveTo(JourneyPhase.CentralCleanup);
                await central.CrashAsync();
            }
            timings.MoveTo(JourneyPhase.CentralStartup);
            await using var restored = await PlatformHostProcess.StartAsync(database.ConnectionString, RootPassword, settings: settings);
            timings.MoveTo(JourneyPhase.CentralAssertions);
            await PlatformSettingsAccessTests.LoginAsync(restored.Client, "journey-root", RootPassword);
            await using var redelivery = new RabbitMqEventBus(broker);
            foreach (var (source, expected) in evidence)
            {
                foreach (var envelope in new[] { expected.PolicyEnvelope, expected.OrdinaryEnvelope })
                {
                    Assert.True((await redelivery.PublishAsync(envelope)).IsSuccess);
                    Assert.True((await redelivery.PublishAsync(envelope)).IsSuccess);
                    var changed = JsonNode.Parse(envelope.Payload)!.AsObject();
                    if (envelope.EventName == OrdinaryEventName(source))
                    { changed["version"] = changed["version"]!.GetValue<long>() + 1; }
                    else { changed["current"]!["maxRecords"] = expected.Policy.MaxRecords + 1; }
                    var conflict = envelope with { Payload = changed.ToJsonString() };
                    Assert.True((await redelivery.PublishAsync(conflict)).IsSuccess);
                    var subscription = new EventSubscription
                    {
                        EventName = envelope.EventName,
                        ConsumerName = envelope.EventName == OrdinaryEventName(source) && source != "platform" ? prefix + "-" + source : prefix,
                    };
                    var rejected = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker, subscription,
                        deadLetter: true, expectedMessageId: envelope.MessageId);
                    Assert.Equal(conflict.Payload, rejected.Payload);
                }
                var page = await FactCapacityPolicyBrokerJourneyTests.WaitForPolicyCountAsync(restored.Client, source, 1);
                FactCapacityPolicyBrokerJourneyTests.AssertEvidence(Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact"),
                    source, expected.PolicyEnvelope, expected.Policy);
                Assert.True(JsonElement.DeepEquals(originals[source], await WaitForOrdinaryAsync(restored.Client, source, expected.OrdinaryEnvelope)));
            }
        }
        finally
        {
            timings.MoveTo(JourneyPhase.BrokerCleanup);
            try { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
            finally
            {
                Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), storage.Parent!.FullName);
                Assert.False(storage.Attributes.HasFlag(FileAttributes.ReparsePoint));
                storage.Delete(recursive: true);
                timings.MoveTo(JourneyPhase.RemainingCleanup);
            }
        }
    }

    private async Task VerifyAsync(string source)
    {
        using var timings = new JourneyPhaseTimings(output);
        timings.MoveTo(JourneyPhase.DatabasePreparation);
        await using var database = await databases.CreateAsync(source);
        await using var centralDatabase = await databases.CreateAsync();
        timings.MoveTo(JourneyPhase.BrokerTopology);
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-recovery", ClientName = prefix };
        var topic = source + ".fact-capacity-policy-changed.v1";
        var subscription = new EventSubscription { EventName = topic, ConsumerName = prefix };
        var tap = subscription with { ConsumerName = prefix + "-tap" };
        var ordinarySubscription = new EventSubscription
        {
            EventName = OrdinaryEventName(source),
            ConsumerName = source == "platform" ? prefix : prefix + "-" + source,
        };
        var ordinaryTap = ordinarySubscription with { ConsumerName = prefix + "-ordinary-tap" };
        var topology = FactCapacityPolicyBrokerJourneyTests.CentralTopology(broker, prefix, [tap, ordinaryTap]);
        var storage = Directory.CreateTempSubdirectory("nsn-recovery-broker-");
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            timings.MoveTo(JourneyPhase.FixtureStartup);
            // These ports only observe owned storage; the source and central HTTP hosts below remain real processes.
            await using var observer = CreateObservationServices(source, database.ConnectionString);
            await using var scope = observer.CreateAsyncScope();
            var outbox = FactRecoveryProtocolTests.GetOutbox(scope.ServiceProvider, source);
            var delivery = FactRecoveryProtocolTests.GetPort(scope.ServiceProvider, source);
            FactCapacityPolicyRequest policy;
            OutboxEntry original;
            OutboxEntry ordinary;
            DateTimeOffset stoppedAt;
            timings.MoveTo(JourneyPhase.SourceStartup);
            await using (var first = await SourceProcess.StartAsync(source, database.ConnectionString, storage.FullName))
            {
                timings.MoveTo(JourneyPhase.SourceAssertions);
                var adjusted = await FactCapacityPolicyBrokerJourneyTests.AdjustAsync(first.Client, source);
                policy = adjusted.Request;
                var beforeCommit = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
                original = Assert.Single(beforeCommit, entry => entry.Id == adjusted.EventId);
                var previous = beforeCommit.Select(entry => entry.Id).ToHashSet();
                await CommitOrdinaryAsync(first.Client, source);
                // Files emits two lifecycle facts; this journey follows the created fact, without claiming both were recovered.
                ordinary = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue))
                    .Where(entry => !previous.Contains(entry.Id) && entry.EventName == ordinarySubscription.EventName)
                    .OrderBy(entry => entry.OccurredAt).First();
                stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
                await using var offline = new RabbitMqEventBus(broker with { HostName = "127.0.0.1", Port = 1 });
                var failed = await new OutboxPublisher(outbox, offline, new FixedClock(stoppedAt), new() { MaxAttempts = 1 }).PublishPendingAsync();
                Assert.True(failed.DeadLettered > 0);
                Assert.Equal("DeadLettered", (await delivery.GetAsync(original.Id)).Value.State);
                Assert.Equal("DeadLettered", (await delivery.GetAsync(ordinary.Id)).Value.State);
                Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
                timings.MoveTo(JourneyPhase.SourceCleanup);
            }
            var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), original.Id, stoppedAt, 0, "dependency-restored");
            var ordinaryRequest = request with { RequestId = Guid.NewGuid(), MessageId = ordinary.Id };
            JsonElement receipt;
            JsonElement ordinaryReceipt;
            timings.MoveTo(JourneyPhase.SourceStartup);
            await using (var recovering = await SourceProcess.StartAsync(source, database.ConnectionString, storage.FullName))
            {
                timings.MoveTo(JourneyPhase.SourceAssertions);
                receipt = await RecoverAsync(recovering.Client, source, request);
                ordinaryReceipt = await RecoverAsync(recovering.Client, source, ordinaryRequest);
                Assert.Equal("Pending", (await delivery.GetAsync(original.Id)).Value.State);
                var pending = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
                var retained = Assert.Single(pending, entry => entry.Id == original.Id);
                Assert.Equal(original.Payload, retained.Payload);
                Assert.Equal(original.EventName, retained.EventName);
                Assert.Equal(original.OccurredAt, retained.OccurredAt);
                Assert.Equal(1, retained.RetryRevision);
                var retainedOrdinary = Assert.Single(pending, entry => entry.Id == ordinary.Id);
                Assert.Equal(ordinary.Payload, retainedOrdinary.Payload);
                Assert.Equal(ordinary.EventName, retainedOrdinary.EventName);
                Assert.Equal(ordinary.OccurredAt, retainedOrdinary.OccurredAt);
                Assert.Equal(1, retainedOrdinary.RetryRevision);
                timings.MoveTo(JourneyPhase.SourceCleanup);
            }
            timings.MoveTo(JourneyPhase.SourceStartup);
            await using (var restarted = await SourceProcess.StartAsync(source, database.ConnectionString, storage.FullName))
            {
                timings.MoveTo(JourneyPhase.SourceAssertions);
                Assert.True(JsonElement.DeepEquals(receipt, await RecoverAsync(restarted.Client, source, request)));
                Assert.True(JsonElement.DeepEquals(ordinaryReceipt, await RecoverAsync(restarted.Client, source, ordinaryRequest)));
                await using var bus = new RabbitMqEventBus(broker);
                var paused = new PausedPublishReceipt(bus, original.Id);
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var publishing = new OutboxPublisher(outbox, paused, new FixedClock(stoppedAt.AddSeconds(1)), new()).PublishPendingAsync(budget.Token);
                try
                {
                    await paused.Confirmed.WaitAsync(budget.Token);
                    Assert.False(publishing.IsCompleted);
                    Assert.Equal("Pending", (await delivery.GetAsync(original.Id)).Value.State);
                    Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "other-dispatcher-failure", stoppedAt, 1));
                    var second = request with { RequestId = Guid.NewGuid(), ExpectedRetryRevision = 1 };
                    var secondReceipt = await RecoverAsync(restarted.Client, source, second);
                    Assert.Equal(2, secondReceipt.GetProperty("retryRevision").ReadHttpInt64());
                    Assert.False(await outbox.MarkFailedAsync(original.Id, "late-old-failure", stoppedAt, 1));
                    paused.Release();
                    Assert.True((await publishing.WaitAsync(budget.Token)).Delivered > 0);
                }
                finally
                {
                    paused.Release();
                    if (!publishing.IsCompleted) { budget.Cancel(); }
                    try { await publishing; }
                    catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                }
                Assert.Equal("Delivered", (await delivery.GetAsync(original.Id)).Value.State);
                Assert.Equal("Delivered", (await delivery.GetAsync(ordinary.Id)).Value.State);
                Assert.False(await outbox.MarkDeadLetteredAsync(original.Id, "late-after-confirmation", stoppedAt, 2));
                Assert.True(JsonElement.DeepEquals(receipt, await RecoverAsync(restarted.Client, source, request)));
                timings.MoveTo(JourneyPhase.SourceCleanup);
            }
            timings.MoveTo(JourneyPhase.BrokerReceive);
            var envelope = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker, tap, expectedMessageId: original.Id);
            Assert.Equal(original.Payload, envelope.Payload);
            Assert.Equal(original.EventName, envelope.EventName);
            Assert.Equal(original.OccurredAt.ToUnixTimeSeconds(), envelope.OccurredAt.ToUnixTimeSeconds());
            var ordinaryEnvelope = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker, ordinaryTap, expectedMessageId: ordinary.Id);
            Assert.Equal(ordinary.Payload, ordinaryEnvelope.Payload);
            Assert.Equal(ordinary.EventName, ordinaryEnvelope.EventName);
            Assert.Equal(ordinary.OccurredAt.ToUnixTimeSeconds(), ordinaryEnvelope.OccurredAt.ToUnixTimeSeconds());
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
            settings["Scheduling__Worker__Enabled"] = "false";
            JsonElement persistedOrdinary;
            timings.MoveTo(JourneyPhase.CentralStartup);
            await using (var central = await PlatformHostProcess.StartAsync(centralDatabase.ConnectionString, RootPassword, settings: settings))
            {
                timings.MoveTo(JourneyPhase.CentralAssertions);
                await PlatformSettingsAccessTests.LoginAsync(central.Client, "journey-root", RootPassword);
                var page = await FactCapacityPolicyBrokerJourneyTests.WaitForPolicyCountAsync(central.Client, source, 1);
                FactCapacityPolicyBrokerJourneyTests.AssertEvidence(Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact"), source, envelope, policy);
                persistedOrdinary = await WaitForOrdinaryAsync(central.Client, source, ordinaryEnvelope);
                timings.MoveTo(JourneyPhase.CentralCleanup);
                await central.CrashAsync();
            }
            timings.MoveTo(JourneyPhase.CentralStartup);
            await using var restored = await PlatformHostProcess.StartAsync(centralDatabase.ConnectionString, RootPassword, settings: settings);
            timings.MoveTo(JourneyPhase.CentralAssertions);
            await PlatformSettingsAccessTests.LoginAsync(restored.Client, "journey-root", RootPassword);
            await using var redelivery = new RabbitMqEventBus(broker);
            Assert.True(JsonElement.DeepEquals(persistedOrdinary, await WaitForOrdinaryAsync(restored.Client, source, ordinaryEnvelope)));
            Assert.True((await redelivery.PublishAsync(envelope)).IsSuccess);
            Assert.True((await redelivery.PublishAsync(envelope)).IsSuccess);
            var changed = JsonNode.Parse(envelope.Payload)!.AsObject();
            changed["current"]!["maxRecords"] = policy.MaxRecords + 1;
            var conflict = envelope with { Payload = changed.ToJsonString() };
            Assert.True((await redelivery.PublishAsync(conflict)).IsSuccess);
            // Prefetch=1: the conflicting delivery reaches DLQ only after both preceding duplicates were handled.
            var rejected = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker, subscription, deadLetter: true, expectedMessageId: original.Id);
            Assert.Equal(conflict.Payload, rejected.Payload);
            var final = await FactCapacityPolicyBrokerJourneyTests.WaitForPolicyCountAsync(restored.Client, source, 1);
            FactCapacityPolicyBrokerJourneyTests.AssertEvidence(Assert.Single(final.GetProperty("data").EnumerateArray()).GetProperty("fact"), source, envelope, policy);
            Assert.True((await redelivery.PublishAsync(ordinaryEnvelope)).IsSuccess);
            Assert.True((await redelivery.PublishAsync(ordinaryEnvelope)).IsSuccess);
            var ordinaryChanged = JsonNode.Parse(ordinaryEnvelope.Payload)!.AsObject();
            ordinaryChanged["version"] = ordinaryChanged["version"]!.GetValue<long>() + 1;
            var ordinaryConflict = ordinaryEnvelope with { Payload = ordinaryChanged.ToJsonString() };
            Assert.True((await redelivery.PublishAsync(ordinaryConflict)).IsSuccess);
            var ordinaryRejected = await FactCapacityPolicyBrokerJourneyTests.ReadEnvelopeAsync(broker, ordinarySubscription,
                deadLetter: true, expectedMessageId: ordinary.Id);
            Assert.Equal(ordinaryConflict.Payload, ordinaryRejected.Payload);
            Assert.True(JsonElement.DeepEquals(persistedOrdinary, await WaitForOrdinaryAsync(restored.Client, source, ordinaryEnvelope)));
        }
        finally
        {
            timings.MoveTo(JourneyPhase.BrokerCleanup);
            try { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
            finally
            {
                Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), storage.Parent!.FullName);
                Assert.False(storage.Attributes.HasFlag(FileAttributes.ReparsePoint));
                storage.Delete(recursive: true);
                timings.MoveTo(JourneyPhase.RemainingCleanup);
            }
        }
    }

    private const string RootPassword = "recovery-broker-root-test";

    private static string OrdinaryEventName(string source) => source switch
    {
        "platform" => SettingCommittedV1.Name,
        "identity" => IdentityEntityCommittedV1.Name,
        "files" => StoredFileCommittedV1.Name,
        "scheduling" => PlanCommittedV1.Name,
        "costing" => CostSheetCommittedV1.Name,
        "pricing" => PriceQuoteCommittedV1.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static async Task CommitOrdinaryAsync(HttpClient client, string source)
    {
        using var bytes = new ByteArrayContent([8, 4, 2]);
        using var response = source switch
        {
            "platform" => await client.PutAsJsonAsync(new Uri("/api/platform/settings/recovery.broker", UriKind.Relative),
                new { value = "original-broker-content" }),
            "identity" => await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative),
                new { userName = "recovery-broker-user", password = "recovery-broker-user-test" }),
            "files" => await client.PostAsync(new Uri("/api/files?name=recovery-broker.bin", UriKind.Relative), bytes),
            "scheduling" => await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
            {
                code = "recovery-broker-plan",
                intervalSeconds = 3600,
                firstRunInSeconds = 3600,
                targetKind = "costing.recalculate",
                targetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            }),
            "costing" => await client.PostAsJsonAsync(new Uri("/api/costing/cost", UriKind.Relative), new
            {
                requestId = Guid.NewGuid(),
                itemId = Guid.NewGuid(),
                expectedVersion = "0",
                purchaseCost = 80m,
                freightCost = 20m,
            }),
            "pricing" => await client.PostAsJsonAsync(new Uri("/api/pricing/cost", UriKind.Relative), new
            {
                requestId = Guid.NewGuid(),
                itemId = Guid.NewGuid(),
                expectedVersion = "0",
                cost = 80m,
                feeRate = 0.2m,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        Assert.Equal(source == "platform" ? HttpStatusCode.NoContent
            : source is "costing" or "pricing" ? HttpStatusCode.Accepted : HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> WaitForOrdinaryAsync(HttpClient client, string source, EventEnvelope envelope)
    {
        using var payload = JsonDocument.Parse(envelope.Payload);
        var message = payload.RootElement;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/auditing/entries?source={source}&limit=100", UriKind.Relative), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var matches = page.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("fact"))
                .Where(fact => fact.GetProperty("messageId").GetGuid() == envelope.MessageId).ToArray();
            if (matches.Length > 0)
            {
                var fact = Assert.Single(matches);
                Assert.Equal(source, fact.GetProperty("source").GetString());
                Assert.Equal(envelope.EventName, fact.GetProperty("eventName").GetString());
                Assert.Equal(message.GetProperty("version").GetInt64(), fact.GetProperty("subjectVersion").ReadHttpInt64());
                Assert.Equal(message.GetProperty("actorId").GetString(), fact.GetProperty("actorId").GetString());
                Assert.Equal(message.GetProperty("traceId").GetString(), fact.GetProperty("traceId").GetString());
                Assert.Equal(message.GetProperty("correlationId").GetString(), fact.GetProperty("correlationId").GetString());
                Assert.InRange(message.GetProperty("occurredAt").GetDateTimeOffset().UtcTicks
                    - fact.GetProperty("occurredAt").GetDateTimeOffset().UtcTicks, 0L, 9L);
                return fact.Clone();
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<JsonElement> RecoverAsync(HttpClient client, string source, FactDeliveryRecoveryRequest request)
    {
        using var response = await client.PostAsJsonAsync(new Uri($"/api/{source}/audit-deliveries/{request.MessageId}/retry", UriKind.Relative), new
        {
            request.RequestId,
            request.ExpectedDeadLetteredAt,
            request.ExpectedRetryRevision,
            request.Reason,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private sealed class PausedPublishReceipt(IEventBus bus, Guid target) : IEventBus
    {
        private readonly TaskCompletionSource _confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Confirmed => _confirmed.Task;
        public void Release() => _released.TrySetResult();

        public async Task<Result> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var result = await bus.PublishAsync(envelope, cancellationToken);
            if (result.IsSuccess && envelope.MessageId == target)
            {
                _confirmed.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private static ServiceProvider CreateObservationServices(string source, string connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddSingleton<IIntegrationEventSerializer, SystemTextJsonIntegrationEventSerializer>();
        services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        switch (source)
        {
            case "platform": services.AddPlatformPostgresStorage(connection); break;
            case "identity": services.AddIdentityEntityFrameworkStorage(connection); break;
            case "files": services.AddFilesPostgresMetadata(connection); break;
            case "scheduling": services.AddSchedulingPostgresStorage(connection); break;
            case "costing": services.AddCostingPostgres(connection); break;
            case "pricing": services.AddPricingPostgres(connection); break;
            default: throw new ArgumentOutOfRangeException(nameof(source));
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class SourceProcess(IAsyncDisposable host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public static async Task<SourceProcess> StartAsync(string source, string connection, string filesRoot)
        {
            if (source is "costing" or "pricing")
            {
                var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
                var context = source == "costing" ? "Costing" : "Pricing";
                var process = await BusinessProcess.StartAsync(assembly, context, connection);
                process.Authenticate();
                return new(process, process.Client);
            }
            var platform = await PlatformHostProcess.StartAsync(connection, RootPassword, filesRoot,
                settings: new Dictionary<string, string> { ["Scheduling__Worker__Enabled"] = "false" });
            try
            {
                await PlatformSettingsAccessTests.LoginAsync(platform.Client, "journey-root", RootPassword);
                return new(platform, platform.Client);
            }
            catch { await platform.DisposeAsync(); throw; }
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
