using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Notifications.Application;
using NexusStackNext.Notifications.Contracts;
using NexusStackNext.Notifications.Infrastructure;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SmtpEmailAuditJourneyTests(JourneyDatabaseTemplates databases)
{
    [AuditBrokerFact]
    public async Task Smtp_committed_facts_cross_rabbitmq_without_disclosing_mail_and_preserve_system_origin()
    {
        await using var receiver = new TestSmtpReceiver();
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-mail", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = EmailDeliveryCommittedV1.Name, ConsumerName = prefix + "-notifications" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
        settings["DOTNET_ENVIRONMENT"] = "Testing";
        settings["Notifications__Enabled"] = "true";
        settings["ConnectionStrings__Notifications"] = database.ConnectionString;
        settings["Notifications__Smtp__Host"] = "127.0.0.1";
        settings["Notifications__Smtp__Port"] = receiver.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["Notifications__Smtp__Sender"] = "sender@example.test";
        settings["Notifications__Smtp__Security"] = "None";
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "smtp-audit-root-password", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "smtp-audit-root-password");
            host.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "smtp-audit-200");
            var id = Guid.NewGuid();
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/notifications/emails", UriKind.Relative),
                new SubmitEmail(id, ["private-recipient@example.test"], "private subject", "private report", null, []));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await host.Client.GetAsync(new Uri($"/api/auditing/entries?source=notifications&subjectId={id:D}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                facts = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(entry => entry.GetProperty("fact").Clone()).ToArray();
                if (facts.Length == 3) { break; }
                await Task.Delay(100, budget.Token);
            }
            var submitted = Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "notifications.email.submitted");
            var completed = Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "notifications.email.accepted");
            Assert.Equal(1, submitted.GetProperty("subjectVersion").ReadHttpInt64());
            Assert.Equal(3, completed.GetProperty("subjectVersion").ReadHttpInt64());
            Assert.Equal(JsonValueKind.Null, completed.GetProperty("actorId").ValueKind);
            Assert.Equal(submitted.GetProperty("execution").GetProperty("rootOperationId").GetGuid(), completed.GetProperty("execution").GetProperty("rootOperationId").GetGuid());
            Assert.Equal("smtp-audit-200", completed.GetProperty("correlationId").GetString());
            var evidence = JsonSerializer.Serialize(facts);
            Assert.DoesNotContain("private-recipient", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("private subject", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("private report", evidence, StringComparison.Ordinal);
            JsonElement[] operations;
            while (true)
            {
                using var response = await host.Client.GetAsync(new Uri($"/api/auditing/operations?traceId={submitted.GetProperty("traceId").GetString()}", UriKind.Relative), budget.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                operations = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(entry => entry.Clone()).ToArray();
                if (operations.Any(item => item.GetProperty("metadata").GetProperty("action").GetString() == "notifications.email.deliver"
                    && item.GetProperty("outcome").GetString() == "completed")
                    && operations.Any(item => item.GetProperty("metadata").GetProperty("action").GetString() == "notifications.email.submit"
                        && item.GetProperty("outcome").GetString() == "accepted")) { break; }
                await Task.Delay(100, budget.Token);
            }
            Assert.Contains(operations, item => item.GetProperty("metadata").GetProperty("action").GetString() == "notifications.email.submit"
                && item.GetProperty("outcome").GetString() == "accepted");
            var observationEvidence = JsonSerializer.Serialize(operations);
            Assert.DoesNotContain("private-recipient", observationEvidence, StringComparison.Ordinal);
            Assert.DoesNotContain("private subject", observationEvidence, StringComparison.Ordinal);
            Assert.DoesNotContain("private report", observationEvidence, StringComparison.Ordinal);
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }
}
