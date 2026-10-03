using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Infrastructure;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryIdentityAuditJourneyTests
{
    [AuditBrokerFact]
    public async Task CommittedSecurityRejection_ReachesCentralThroughTheHostsMemoryOutbox()
    {
        await using var central = await IdentityJourneyDatabase.CreateAsync();
        await central.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-memory-identity", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = IdentityEntityCommittedV1.Name, ConsumerName = prefix + "-identity" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
            var sourceSettings = settings.ToDictionary(item => item.Key.Replace("__", ":", StringComparison.Ordinal), item => (string?)item.Value, StringComparer.Ordinal);
            sourceSettings["Auditing:Messaging:Enabled"] = "false";
            long id;
            await using (var source = new MemorySourceApp(sourceSettings) { SchedulingWorkerEnabled = false })
            {
                using var client = source.CreateClient();
                using var create = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new { userName = "memory-broker-user", password = "private-memory-password" });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                id = (await create.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
                using var rejected = await client.PostAsJsonAsync(new Uri("/api/identity/login", UriKind.Relative), new { userName = "memory-broker-user", password = "wrong-memory-password" });
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                await using var scope = source.Services.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(IdentityEntityFrameworkServiceCollectionExtensions.OutboxKey);
                await GatewayResilienceTests.EventuallyAsync(async () => (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Count == 0);
            }
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "memory-identity-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "memory-identity-root");
            await GatewayResilienceTests.EventuallyAsync(async () =>
            {
                using var response = await reader.Client.GetAsync(new Uri($"/api/auditing/entries?source=identity&subjectType=user&subjectId={id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var facts = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(item => item.GetProperty("fact")).ToArray();
                if (facts.Length != 2) { return false; }
                Assert.Equal(new[] { "identity.user.created", "identity.user.login-failed" }, facts.Select(fact => fact.GetProperty("action").GetString()).Order(StringComparer.Ordinal));
                Assert.All(facts, fact => Assert.DoesNotContain("memory-broker-user", fact.GetRawText(), StringComparison.Ordinal));
                return true;
            });
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private sealed class MemorySourceApp(Dictionary<string, string?> settings) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }
    }
}
