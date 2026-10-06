using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyDeadlineTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task FilesPolicyDeliveryConfirmation_PreservesTheFull24HourMinimum_WhenAckHasSubMicrosecondPrecision()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("files");
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var confirmedAt = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
        var minimumDeadline = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, initial.MaxRecords + 1, initial.MaxPayloadBytes,
            initial.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(request, "test-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        Assert.NotNull(accepted.Value.EventId);
        await outbox.MarkDeliveredAsync(accepted.Value.EventId.Value, confirmedAt);
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(1, before.ControlCapacity.RetainedRecords);
        Assert.Equal(0, await cleanup.CleanupAsync(1, minimumDeadline.AddTicks(-1)));
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", minimumDeadline.AddTicks(-1), null)).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, minimumDeadline.AddTicks(9)));
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, after.ControlCapacity.RetainedRecords);
        Assert.Equal(0, after.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        Assert.Equal(before.Business, after.Business);
    }

    [PostgresFact]
    public async Task AllPostgresSourceConfirmations_KeepTheFirstAckAndPreserve24Hours_InTheirOwnedAdapters()
    {
        await using var platformDatabase = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(platformDatabase.ConnectionString, schedulingWorkerEnabled: false);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            foreach (var source in new[] { "platform", "identity", "files", "scheduling" })
            {
                await VerifyFirstAckAsync(scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(source),
                    scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>(source),
                    scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(source));
            }
        }
        await using var costingDatabase = await databases.CreateAsync("costing");
        await using var costing = CreateBusinessModule(costingDatabase.ConnectionString, "Costing");
        await using (var scope = costing.Services.CreateAsyncScope())
        {
            await VerifyFirstAckAsync(scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("costing"),
                scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("costing"),
                scope.ServiceProvider.GetRequiredService<IOutboxStore>());
        }
        await using var pricingDatabase = await databases.CreateAsync("pricing");
        await using var pricing = CreateBusinessModule(pricingDatabase.ConnectionString, "Pricing");
        await using (var scope = pricing.Services.CreateAsyncScope())
        {
            await VerifyFirstAckAsync(scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("pricing"),
                scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("pricing"),
                scope.ServiceProvider.GetRequiredService<IOutboxStore>());
        }
    }

    private static WebApplication CreateBusinessModule(string connection, string context)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:Messaging:Enabled"] = "false",
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false"
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        if (context == "Costing") { builder.Services.AddCostingModule(builder.Configuration); }
        else { builder.Services.AddPricingModule(builder.Configuration); }
        return builder.Build();
    }

    private static async Task VerifyFirstAckAsync(ICommittedFactCapacityPolicyStore policies, ICommittedFactCapacityPolicyCleanup cleanup, IOutboxStore outbox)
    {
        var initial = (await policies.ReadPolicyAsync()).Value;
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var confirmedAt = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.FromHours(3)).AddTicks(9);
        var minimumDeadline = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero).AddTicks(9);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), initial.PolicyRevision, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(request, "test-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        Assert.NotNull(accepted.Value.EventId);
        await outbox.MarkDeliveredAsync(accepted.Value.EventId.Value, confirmedAt);
        await outbox.MarkDeliveredAsync(accepted.Value.EventId.Value, confirmedAt.AddDays(20));
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, await cleanup.CleanupAsync(1, minimumDeadline.AddTicks(-1)));
        Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
        Assert.Equal(1, await cleanup.CleanupAsync(1, minimumDeadline.AddTicks(1)));
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, after.ControlCapacity.RetainedRecords);
        Assert.Equal(0, after.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(before.PolicyRevision, after.PolicyRevision);
        Assert.Equal(before.Business, after.Business);
    }

    [PostgresFact]
    public async Task FilesPolicyReceipt_NeverCleansBeforeItsAdvertisedDeadline_WhenAcceptedTimeHasSubMicrosecondPrecision()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("files");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
        var advertisedDeadline = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, initial.MaxRecords, initial.MaxPayloadBytes,
            initial.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(request, "test-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        Assert.False(accepted.Value.Changed);
        Assert.Equal(advertisedDeadline, accepted.Value.RetainUntil);
        Assert.Equal(0, await cleanup.CleanupAsync(1, advertisedDeadline.AddTicks(-1)));
        Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", advertisedDeadline.AddTicks(-1), null)).Value);
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        Assert.Equal(1, await cleanup.CleanupAsync(1, advertisedDeadline.AddTicks(9)));
        Assert.Equal(0, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
    }
}
