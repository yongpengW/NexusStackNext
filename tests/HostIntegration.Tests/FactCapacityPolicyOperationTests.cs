using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FactCapacityPolicyOperationTests
{
    [Fact]
    public async Task PlatformMemory_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, "platform", "platform");
    }

    [Fact]
    public async Task IdentityMemory_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, "identity", "platform");
    }

    [Fact]
    public async Task FilesMemory_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, "files", "platform");
    }

    [Fact]
    public async Task SchedulingMemory_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, "scheduling", "platform");
    }

    [PostgresFact]
    public async Task CostingPostgres_FromRealProcess_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var settings = new Dictionary<string, string>
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = database.ConnectionString,
        };
        await using var producer = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
            "Costing", database.ConnectionString, settings: settings);
        producer.Authenticate();
        var services = new ServiceCollection();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString);
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await VerifyAsync(producer.Client, provider, "costing", "costing");
    }

    [PostgresFact]
    public async Task PricingPostgres_FromRealProcess_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        await OperationJournalDatabase.MigrateAsync(database.ConnectionString);
        var settings = new Dictionary<string, string>
        {
            ["OperationJournal__Storage__Provider"] = "Postgres",
            ["ConnectionStrings__OperationJournal"] = database.ConnectionString,
        };
        await using var producer = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location,
            "Pricing", database.ConnectionString, settings: settings);
        producer.Authenticate();
        var services = new ServiceCollection();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString);
        services.AddOperationJournalPostgresStorage(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await VerifyAsync(producer.Client, provider, "pricing", "pricing");
    }

    [PostgresFact]
    public Task PlatformPostgres_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations() => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations() => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations() => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_AdjustmentReplayAndRejection_ProduceSafeDistinctObservations() => VerifyPlatformPostgresAsync("scheduling");

    private static async Task VerifyPlatformPostgresAsync(string context)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString,
            PlatformAppWithRootAccount.RootPassword, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", PlatformAppWithRootAccount.RootPassword);
        await VerifyAsync(client, app.Services, context, "platform");
    }

    private static async Task VerifyAsync(HttpClient client, IServiceProvider services, string context, string hostSource)
    {
        var path = new Uri($"/api/{context}/audit-capacity?secret=private-policy-query", UriKind.Relative);
        client.DefaultRequestHeaders.Add("X-Private-Policy-Token", "private-policy-header");
        var request = new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = "1",
            maxRecords = "200000",
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = 16384,
            reason = "operator-adjustment",
            actorId = "private-forged-actor",
            privateNotes = "private-policy-body",
        };
        using var accepted = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadApiDataAsync();
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        using var replay = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetRawText(), (await replay.Content.ReadApiDataAsync()).GetRawText());
        using var rejected = await client.PutAsJsonAsync(path, new
        {
            requestId = Guid.NewGuid(),
            expectedPolicyRevision = "2",
            maxRecords = "200001",
            maxPayloadBytes = "268435456",
            maxRecordPayloadBytes = 16384,
            reason = "private-policy-reason",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await using var scope = services.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        OperationObservedV1[] observations;
        do
        {
            observations = (await journal.ReadPendingAsync(1000, DateTimeOffset.MaxValue, timeout.Token))
                .Select(entry => serializer.Deserialize<OperationObservedV1>(entry.Payload))
                .Where(message => message.HttpMethod == "PUT" && message.RouteTemplate == $"/api/{context}/audit-capacity").ToArray();
            if (observations.Length < 6) { await Task.Delay(25, timeout.Token); }
        } while (observations.Length < 6);
        Assert.Equal(6, observations.Length);
        Assert.All(observations, observation =>
        {
            Assert.Equal(hostSource, observation.Source);
            Assert.NotNull(observation.Metadata);
            Assert.Equal($"{context}.fact-capacity-policy.adjust", observation.Metadata.Action);
            Assert.Equal("调整所属事实容量策略", observation.Metadata.Description);
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
        var completed = observations.Where(observation => observation.Phase == "finished").ToArray();
        Assert.Equal(2, completed.Count(observation => observation.StatusCode == 200 && observation.Outcome == "completed"));
        Assert.Single(completed, observation => observation.StatusCode == 400 && observation.Outcome == "rejected");
        var sourceOutbox = context is "costing" or "pricing"
            ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
            : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var fact = Assert.Single(await sourceOutbox.ReadPendingAsync(1000, DateTimeOffset.MaxValue),
            entry => entry.EventName == $"{context}.fact-capacity-policy-changed.v1");
        using var payload = JsonDocument.Parse(fact.Payload);
        var operationId = payload.RootElement.GetProperty("execution").GetProperty("operationId").GetGuid();
        var committingOperation = Assert.Single(completed, observation => observation.OperationId == operationId);
        Assert.Equal(200, committingOperation.StatusCode);
        Assert.Equal(payload.RootElement.GetProperty("actorId").GetString(), committingOperation.ActorId);
        Assert.NotNull(committingOperation.ActorId);
        Assert.Equal(receipt.GetProperty("eventId").GetGuid(), fact.Id);
    }
}
