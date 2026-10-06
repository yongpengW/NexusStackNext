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
public sealed class FactCapacityPolicyProtocolTests(JourneyDatabaseTemplates databases)
{
    [Fact]
    public Task PlatformMemory_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyMemoryAsync("platform");

    [PostgresFact]
    public Task PlatformPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyPlatformPostgresAsync("platform");

    [Fact]
    public async Task IdentityMemory_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await VerifyAsync(scope.ServiceProvider, "identity");
    }

    [PostgresFact]
    public async Task IdentityPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await VerifyAsync(scope.ServiceProvider, "identity");
    }

    [Fact]
    public Task FilesMemory_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyMemoryAsync("files");

    [Fact]
    public Task SchedulingMemory_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyMemoryAsync("scheduling");

    [PostgresFact]
    public Task FilesPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public Task CostingPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyBusinessPostgresAsync("Costing", "costing");

    [PostgresFact]
    public Task PricingPostgres_ConditionalPolicyProtocol_PreservesDecisionsAndSafelyReusesExpiredIdentity()
        => VerifyBusinessPostgresAsync("Pricing", "pricing");

    private static async Task VerifyMemoryAsync(string context)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await VerifyAsync(scope.ServiceProvider, context);
    }

    private async Task VerifyPlatformPostgresAsync(string context)
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await VerifyAsync(scope.ServiceProvider, context);
    }

    private async Task VerifyBusinessPostgresAsync(string configurationContext, string context)
    {
        await using var database = await databases.CreateAsync(context);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{configurationContext}"] = database.ConnectionString,
            [$"{configurationContext}:Worker:Enabled"] = "false",
            [$"{configurationContext}:Messaging:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        if (context == "costing") { builder.Services.AddCostingModule(builder.Configuration); }
        else { builder.Services.AddPricingModule(builder.Configuration); }
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await VerifyAsync(scope.ServiceProvider, context);
    }

    private static async Task VerifyAsync(IServiceProvider services, string context)
    {
        var policies = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var cleanup = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>(context);
        var outbox = context is "costing" or "pricing" ? services.GetRequiredService<IOutboxStore>()
            : services.GetRequiredKeyedService<IOutboxStore>(context);
        var initial = (await policies.ReadPolicyAsync()).Value;
        var originalFacts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, initial.PolicyRevision);
        Assert.Equal(0, initial.ControlCapacity.RetainedRecords);
        var requests = new[]
        {
            new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 2, 16384, 16384, "operator-adjustment"),
            new FactCapacityPolicyRequest(Guid.NewGuid(), 1, 3, 16384, 16384, "operator-adjustment"),
        };
        var results = await Task.WhenAll(requests.Select(request => Task.Run(() => policies.AdjustAsync(request, "test-operator", now, null))));
        var winnerIndex = Assert.Single(Enumerable.Range(0, results.Length), index => results[index].IsSuccess);
        var winner = results[winnerIndex].Value;
        Assert.Equal(context + ".audit_policy.conflict", Assert.Single(results, result => result.IsFailure).Error.Code);
        Assert.True(winner.Changed);
        Assert.Equal(2, winner.PolicyRevision);
        Assert.Equal(now.AddDays(7), winner.RetainUntil);
        Assert.NotNull(winner.EventId);
        Assert.NotEqual(winner.RequestId, winner.EventId.Value);
        var accepted = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, accepted.PolicyRevision);
        Assert.Equal(1, accepted.ControlCapacity.RetainedRecords);
        Assert.Equal(initial.RetainedRecords, accepted.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, accepted.RetainedPayloadBytes);
        var acceptedFact = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == winner.EventId);
        Assert.Equal(context + ".fact-capacity-policy-changed.v1", acceptedFact.EventName);
        var conflictingContent = await policies.AdjustAsync(requests[winnerIndex] with { MaxRecords = 999 }, "test-operator", now, null);
        Assert.True(conflictingContent.IsFailure);
        Assert.Equal(context + ".audit_policy.conflict", conflictingContent.Error.Code);
        var conflictingActor = await policies.AdjustAsync(requests[winnerIndex], "another-operator", now, null);
        Assert.True(conflictingActor.IsFailure);
        Assert.Equal(context + ".audit_policy.conflict", conflictingActor.Error.Code);
        Assert.Equal(accepted, (await policies.ReadPolicyAsync()).Value);
        var revert = new FactCapacityPolicyRequest(Guid.NewGuid(), 2, initial.MaxRecords,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var reverted = await policies.AdjustAsync(revert, "test-operator", now.AddMinutes(1), null);
        Assert.True(reverted.IsSuccess);
        Assert.Equal(3, reverted.Value.PolicyRevision);
        Assert.NotNull(reverted.Value.EventId);
        Assert.NotEqual(winner.EventId, reverted.Value.EventId);
        var stale = await policies.AdjustAsync(requests[winnerIndex] with { RequestId = Guid.NewGuid() }, "test-operator", now, null);
        Assert.True(stale.IsFailure);
        Assert.Equal(context + ".audit_policy.conflict", stale.Error.Code);
        Assert.Equal(winner, (await policies.AdjustAsync(requests[winnerIndex], "test-operator", now.AddDays(1), null)).Value);
        var noOpRequest = revert with { RequestId = Guid.NewGuid(), ExpectedPolicyRevision = 3 };
        var noOp = await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(3), null);
        Assert.True(noOp.IsSuccess);
        Assert.False(noOp.Value.Changed);
        Assert.Null(noOp.Value.EventId);
        Assert.Equal(3, noOp.Value.PolicyRevision);
        Assert.Equal(now.AddDays(10), noOp.Value.RetainUntil);
        var final = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(3, final.PolicyRevision);
        Assert.Equal(initial.Business, final.Business);
        Assert.Equal(3, final.ControlCapacity.RetainedRecords);
        var pending = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, pending.Count(entry => entry.EventName == context + ".fact-capacity-policy-changed.v1"));
        Assert.Equal(originalFacts, pending.Where(entry => entry.EventName != context + ".fact-capacity-policy-changed.v1"));
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(7)));
        // An earliest retention deadline does not invalidate a receipt still retained for pending delivery.
        Assert.Equal(winner, (await policies.AdjustAsync(requests[winnerIndex], "test-operator", now.AddDays(8), null)).Value);
        await outbox.MarkDeliveredAsync(winner.EventId.Value, now.AddDays(8));
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(8).AddHours(23)));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(9)));
        var released = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, released.ControlCapacity.RetainedRecords);
        Assert.Equal(3, released.PolicyRevision);
        Assert.Equal(initial.Business, released.Business);
        var expired = await policies.AdjustAsync(requests[winnerIndex], "test-operator", now.AddDays(9), null);
        Assert.True(expired.IsFailure);
        Assert.Equal(context + ".audit_policy.conflict", expired.Error.Code);
        var reused = requests[winnerIndex] with { ExpectedPolicyRevision = 3, MaxRecords = 4 };
        // The caller discards its successful reply, then recovers by repeating the exact request.
        _ = await policies.AdjustAsync(reused, "test-operator", now.AddDays(9), null);
        var recovered = await policies.AdjustAsync(reused, "test-operator", now.AddDays(10), null);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(4, recovered.Value.PolicyRevision);
        Assert.Equal(now.AddDays(9), recovered.Value.AcceptedAt);
        Assert.NotNull(recovered.Value.EventId);
        Assert.NotEqual(winner.EventId, recovered.Value.EventId);
        Assert.NotEqual(winner.RequestId, recovered.Value.EventId.Value);
        Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue), entry => entry.Id == recovered.Value.EventId);
        Assert.Equal(noOp.Value, (await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(10), null)).Value);
        var current = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(4, current.PolicyRevision);
        Assert.Equal(3, current.ControlCapacity.RetainedRecords);
        Assert.Equal(initial.RetainedRecords, current.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, current.RetainedPayloadBytes);
    }
}
