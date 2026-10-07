using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactDeliveryRecoveryOperationTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("identity")]
    [InlineData("files")]
    [InlineData("scheduling")]
    public async Task PlatformHostMemory_RecoveryReplayAndRejectionProduceSafeDistinctObservations(string source)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, source, "platform");
    }

    [PostgresFact]
    public Task PlatformPostgres_RecoveryReplayAndRejectionProduceSafeDistinctObservations()
        => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_RecoveryReplayAndRejectionProduceSafeDistinctObservations()
        => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_RecoveryReplayAndRejectionProduceSafeDistinctObservations()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_RecoveryReplayAndRejectionProduceSafeDistinctObservations()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public Task CostingPostgres_RealProcessRecoveryReplayAndRejectionPreserveSafeDistinctObservations()
        => VerifyBusinessPostgresAsync("costing");

    [PostgresFact]
    public Task PricingPostgres_RealProcessRecoveryReplayAndRejectionPreserveSafeDistinctObservations()
        => VerifyBusinessPostgresAsync("pricing");

    [Fact]
    public async Task PlatformMemory_RecoveryReplayAndRejectionProduceSafeDistinctObservationsAndOriginalAssociation()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, "platform", "platform");
    }

    private async Task VerifyPlatformPostgresAsync(string source)
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, PlatformAppWithRootAccount.RootPassword,
            schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, source, "platform");
    }

    private async Task VerifyBusinessPostgresAsync(string source)
    {
        await using var database = await databases.CreateAsync(source);
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        var settings = new Dictionary<string, string>
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = database.ConnectionString,
        };
        await using var host = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing",
            database.ConnectionString, settings: settings);
        host.Authenticate();
        var services = new ServiceCollection();
        services.AddNexusStackApplication();
        if (source == "costing") { services.AddCostingPostgres(database.ConnectionString); }
        else { services.AddPricingPostgres(database.ConnectionString); }
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await VerifyAsync(host.Client, provider, source, source);
    }

    private static async Task VerifyAsync(HttpClient client, IServiceProvider services, string source, string hostSource)
    {
        await using var scope = services.CreateAsyncScope();
        var policyPath = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initialResponse = await client.GetAsync(policyPath);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        using var changed = await client.PutAsJsonAsync(policyPath, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = initial.GetProperty("policyRevision").GetString(),
            maxRecords = initial.GetProperty("maxRecords").GetString(),
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = initial.GetProperty("maxRecordPayloadBytes").GetInt32() + 1,
            reason = "operator-adjustment",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var messageId = (await changed.Content.ReadApiDataAsync()).GetProperty("eventId").GetGuid();
        var sourceOutbox = source is "costing" or "pricing"
            ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
            : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source);
        var original = Assert.Single(await sourceOutbox.ReadPendingAsync(1000, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        var stoppedAt = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var publisher = new OutboxPublisher(sourceOutbox, new FailingEventBus(), new FixedClock(stoppedAt), new() { MaxAttempts = 1 });
        Assert.True((await publisher.PublishPendingAsync()).DeadLettered > 0);
        var requestId = Guid.NewGuid();
        var retry = new Uri($"/api/{source}/audit-deliveries/{messageId}/retry?secret=private-recovery-query", UriKind.Relative);
        client.DefaultRequestHeaders.Add("X-Private-Recovery-Token", "private-recovery-header");
        var request = new
        {
            requestId,
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "0",
            reason = "dependency-restored",
            actorId = "private-forged-actor",
            privateNotes = "private-recovery-body",
            execution = new { operationId = Guid.Empty, source = "private-forged-origin" },
        };
        using var accepted = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        using var replay = await client.PostAsJsonAsync(retry, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var rejected = await client.PostAsJsonAsync(retry, new
        {
            requestId = Guid.NewGuid(),
            expectedDeadLetteredAt = stoppedAt,
            expectedRetryRevision = "1",
            reason = "private-recovery-reason",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        OperationObservedV1[] observations;
        do
        {
            observations = (await journal.ReadPendingAsync(1000, DateTimeOffset.MaxValue, timeout.Token))
                .Select(entry => serializer.Deserialize<OperationObservedV1>(entry.Payload))
                .Where(observation => observation.HttpMethod == "POST"
                    && observation.RouteTemplate == $"/api/{source}/audit-deliveries/{{messageId:guid}}/retry").ToArray();
            if (observations.Length < 6) { await Task.Delay(25, timeout.Token); }
        } while (observations.Length < 6);
        Assert.Equal(6, observations.Length);
        Assert.All(observations, observation =>
        {
            Assert.Equal(hostSource, observation.Source);
            Assert.NotNull(observation.Metadata);
            Assert.Equal($"{source}.fact-delivery.recover", observation.Metadata.Action);
            Assert.Equal("恢复所属事实投递", observation.Metadata.Description);
            Assert.Equal("endpoint", observation.Metadata.ExecutionRole);
            Assert.Equal(observation.OperationId, observation.Metadata.RootOperationId);
            Assert.Equal(hostSource, observation.Metadata.RootSource);
            Assert.Null(observation.Metadata.SubjectId);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(observation), StringComparison.Ordinal);
        });
        var operations = observations.GroupBy(observation => observation.OperationId).ToArray();
        Assert.Equal(3, operations.Length);
        Assert.All(operations, operation =>
        {
            Assert.Equal(2, operation.Count());
            Assert.Single(operation, observation => observation.Phase == "started");
            Assert.Single(operation, observation => observation.Phase == "finished");
        });
        var finished = observations.Where(observation => observation.Phase == "finished").ToArray();
        Assert.Equal(2, finished.Count(observation => observation.StatusCode == 200 && observation.Outcome == "completed"));
        Assert.Single(finished, observation => observation.StatusCode == 400 && observation.Outcome == "rejected");
        var origin = receipt.GetProperty("execution");
        var recovery = Assert.Single(finished, observation => observation.OperationId == origin.GetProperty("operationId").GetGuid());
        Assert.Equal(200, recovery.StatusCode);
        Assert.Equal(receipt.GetProperty("actorId").GetString(), recovery.ActorId);
        Assert.Equal(hostSource, origin.GetProperty("source").GetString());
        Assert.Equal(recovery.OperationId, origin.GetProperty("rootOperationId").GetGuid());
        var pending = Assert.Single(await sourceOutbox.ReadPendingAsync(1000, DateTimeOffset.MaxValue), entry => entry.Id == messageId);
        Assert.Equal(original.Id, pending.Id);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.OccurredAt, pending.OccurredAt);
        Assert.Equal(1, pending.RetryRevision);
    }
}
