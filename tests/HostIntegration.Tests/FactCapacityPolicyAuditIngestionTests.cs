using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Events;
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

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyAuditIngestionTests(JourneyDatabaseTemplates databases)
{
    [Fact]
    public async Task PolicyHeaderWithoutTypedAmounts_IsRejectedWithoutRecordingAChangedPlaceholder()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
        var fact = new AuditFact(Guid.NewGuid(), SettingFactCapacityPolicyChangedV1.Name, "platform", "platform.fact-capacity-policy.changed",
            "fact-capacity-policy", "platform", 2, "trusted-operator", DateTimeOffset.UtcNow, "policy-trace", "policy-correlation");
        var accepted = await ingestion.IngestAsync(fact);
        Assert.True(accepted.IsFailure);
        Assert.Equal("auditing.fact.invalid", accepted.Error.Code);
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 100)).Entries);
    }

    [Fact]
    public async Task PlatformPolicyMemory_CommitsTypedAmountsToCentralInvestigation_AndDeduplicatesDelivery()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyIngestionAsync(app.Services, client, "platform", SettingFactCapacityPolicyChangedV1.Name);
    }

    [Fact]
    public async Task IdentityPolicyMemory_CommitsTypedAmountsToCentralInvestigation_AndDeduplicatesDelivery()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyIngestionAsync(app.Services, client, "identity", IdentityFactCapacityPolicyChangedV1.Name);
    }

    [Theory]
    [InlineData("files", FilesFactCapacityPolicyChangedV1.Name)]
    [InlineData("scheduling", SchedulingFactCapacityPolicyChangedV1.Name)]
    public async Task PlatformSourcePolicyMemory_PreservesItsOwnedContractInCentralInvestigation(string source, string eventName)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyIngestionAsync(app.Services, client, source, eventName);
    }

    [PostgresFact]
    public Task CostingPolicy_FromRealSourceProcess_PreservesOwnedContractInCentralInvestigation() => VerifyBusinessSourceAsync("costing");

    [PostgresFact]
    public Task PricingPolicy_FromRealSourceProcess_PreservesOwnedContractInCentralInvestigation() => VerifyBusinessSourceAsync("pricing");

    private async Task VerifyBusinessSourceAsync(string source)
    {
        await using var database = await databases.CreateAsync(source);
        var assembly = source == "costing" ? typeof(CostingHostMarker).Assembly.Location : typeof(PricingHostMarker).Assembly.Location;
        await using var producer = await BusinessProcess.StartAsync(assembly, source == "costing" ? "Costing" : "Pricing", database.ConnectionString);
        producer.Authenticate();
        await using var sourceReader = source == "costing"
            ? TaskOperationTests.CreateCostingApp(database.ConnectionString, null)
            : TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await using var sourceScope = sourceReader.Services.CreateAsyncScope();
        var outbox = sourceScope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await using var central = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = central.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        await VerifyIngestionAsync(central.Services, producer.Client, source,
            source == "costing" ? CostingFactCapacityPolicyChangedV1.Name : PricingFactCapacityPolicyChangedV1.Name, client, outbox);
    }

    [PostgresFact]
    public async Task PlatformPolicyPostgres_PersistsTypedAmountsAndInboxAcrossHostRecreation()
    {
        await using var database = await databases.CreateAsync();
        EventEnvelope envelope;
        await using (var original = new PersistentIdentityApp(database.ConnectionString, "audit-policy-root", schedulingWorkerEnabled: false))
        {
            using var client = original.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-policy-root");
            envelope = await VerifyIngestionAsync(original.Services, client, "platform", SettingFactCapacityPolicyChangedV1.Name);
        }
        await using var recreated = new PersistentIdentityApp(database.ConnectionString, "audit-policy-root", schedulingWorkerEnabled: false);
        using var queryClient = recreated.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(queryClient, "journey-root", "audit-policy-root");
        await using var scope = recreated.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(SettingFactCapacityPolicyChangedV1.Name);
        Assert.True(await processor.HandleAsync(envelope));
        using var query = await queryClient.GetAsync(new Uri("/api/auditing/entries?source=platform&action=platform.fact-capacity-policy.changed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var central = Assert.Single((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()).GetProperty("fact");
        Assert.Equal(envelope.MessageId, central.GetProperty("messageId").GetGuid());
        using var payload = JsonDocument.Parse(envelope.Payload);
        var change = central.GetProperty("capacityPolicyChange");
        Assert.Equal(payload.RootElement.GetProperty("requestId").GetGuid(), change.GetProperty("requestId").GetGuid());
        Assert.Equal(payload.RootElement.GetProperty("current").GetProperty("maxRecords").GetInt64(), change.GetProperty("current").GetProperty("maxRecords").ReadHttpInt64());
    }

    [PostgresFact]
    public async Task PlatformPolicyPostgres_RejectsChangedNumericEvidenceUnderSameMessageId()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "audit-policy-root", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "audit-policy-root");
        var envelope = await VerifyIngestionAsync(app.Services, client, "platform", SettingFactCapacityPolicyChangedV1.Name);
        await using var scope = app.Services.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var message = serializer.Deserialize<SettingFactCapacityPolicyChangedV1>(envelope.Payload);
        var changed = message with { Current = message.Current with { MaxRecords = message.Current.MaxRecords + 10 } };
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(SettingFactCapacityPolicyChangedV1.Name);
        Assert.False(await processor.HandleAsync(envelope with { Payload = serializer.Serialize(changed) }));
        using var query = await client.GetAsync(new Uri("/api/auditing/entries?source=platform&action=platform.fact-capacity-policy.changed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var fact = Assert.Single((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()).GetProperty("fact");
        Assert.Equal(message.Current.MaxRecords, fact.GetProperty("capacityPolicyChange").GetProperty("current").GetProperty("maxRecords").ReadHttpInt64());
    }

    private static async Task<EventEnvelope> VerifyIngestionAsync(IServiceProvider services, HttpClient client, string source, string eventName,
        HttpClient? investigationClient = null, IOutboxStore? sourceOutbox = null)
    {
        var path = new Uri($"/api/{source}/audit-capacity", UriKind.Relative);
        using var initialResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadApiDataAsync();
        var previousRecords = initial.GetProperty("maxRecords").ReadHttpInt64();
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, previousRecords + 1,
            initial.GetProperty("maxPayloadBytes").ReadHttpInt64(), initial.GetProperty("maxRecordPayloadBytes").GetInt32(), "operator-adjustment");
        using var changed = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var receipt = await changed.Content.ReadApiDataAsync();
        await using var scope = services.CreateAsyncScope();
        var outbox = sourceOutbox ?? scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source);
        var fact = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), item => item.Id == receipt.GetProperty("eventId").GetGuid());
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(eventName);
        var envelope = new EventEnvelope { EventName = fact.EventName, MessageId = fact.Id, OccurredAt = fact.OccurredAt, Payload = fact.Payload };
        Assert.True(await processor.HandleAsync(envelope));
        Assert.True(await processor.HandleAsync(envelope));
        using var investigation = await (investigationClient ?? client).GetAsync(new Uri($"/api/auditing/entries?source={source}&action={source}.fact-capacity-policy.changed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, investigation.StatusCode);
        var page = await investigation.Content.ReadFromJsonAsync<JsonElement>();
        var central = Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("fact");
        Assert.Equal(source, central.GetProperty("source").GetString());
        Assert.Equal(fact.Id, central.GetProperty("messageId").GetGuid());
        Assert.Equal(2, central.GetProperty("subjectVersion").ReadHttpInt64());
        using var sourcePayload = JsonDocument.Parse(fact.Payload);
        Assert.Equal(sourcePayload.RootElement.GetProperty("actorId").GetString(), central.GetProperty("actorId").GetString());
        var policy = central.GetProperty("capacityPolicyChange");
        Assert.Equal(request.RequestId, policy.GetProperty("requestId").GetGuid());
        Assert.Equal(2, policy.GetProperty("policyRevision").ReadHttpInt64());
        Assert.Equal("operator-adjustment", policy.GetProperty("reason").GetString());
        Assert.Equal(previousRecords, policy.GetProperty("previous").GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(previousRecords + 1, policy.GetProperty("current").GetProperty("maxRecords").ReadHttpInt64());
        Assert.Equal(request.MaxPayloadBytes, policy.GetProperty("current").GetProperty("maxPayloadBytes").ReadHttpInt64());
        Assert.Equal(request.MaxRecordPayloadBytes, policy.GetProperty("current").GetProperty("maxRecordPayloadBytes").GetInt32());
        return envelope;
    }
}
