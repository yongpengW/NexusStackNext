using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Files.Endpoints;
using NexusStackNext.Identity.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PostgresFactCapacityPolicyRevisionTests
{
    [PostgresFact]
    public Task PlatformPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Platform", "platform");

    [PostgresFact]
    public Task IdentityPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Identity", "identity");

    [PostgresFact]
    public Task FilesPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Files", "files");

    [PostgresFact]
    public Task SchedulingPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Scheduling", "scheduling");

    [PostgresFact]
    public Task CostingPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Costing", "costing");

    [PostgresFact]
    public Task PricingPostgres_MaximumRevision_RejectsChangesAndRetainsNoOpDecision()
        => VerifyAsync("Pricing", "pricing");

    private static async Task VerifyAsync(string configurationContext, string context)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        if (context == "costing") { await CostingDatabase.MigrateAsync(database.ConnectionString); }
        else if (context == "pricing") { await PricingDatabase.MigrateAsync(database.ConnectionString); }
        else { await database.MigrateAsync(); }
        if (context is not ("platform" or "identity" or "files" or "scheduling" or "costing" or "pricing"))
        { throw new ArgumentException("Unknown owned test schema.", nameof(context)); }
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            // Arrange a valid persisted boundary; verify behavior only through the actual module's public ports.
            await using var command = new NpgsqlCommand($"""
                UPDATE {context}.fact_policy_control SET "PolicyRevision" = @revision
                    WHERE "Id" = 1 AND "PolicyRevision" = 1 AND "RetainedRecords" = 0 AND "RetainedPayloadBytes" = 0
                """, connection);
            command.Parameters.AddWithValue("revision", long.MaxValue);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "policy-revision-test-signing-key-long-enough-for-hs256",
            [$"{configurationContext}:Storage:Provider"] = "Postgres",
            [$"ConnectionStrings:{configurationContext}"] = database.ConnectionString,
            [$"{configurationContext}:Messaging:Enabled"] = "false",
            [$"{configurationContext}:Worker:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{configurationContext}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-revision-" + Guid.NewGuid().ToString("N")),
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 21 });
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        switch (context)
        {
            case "platform": builder.Services.AddPlatformModule(builder.Configuration, builder.Environment); break;
            case "identity": builder.Services.AddIdentityModule(builder.Configuration, builder.Environment); break;
            case "files": builder.Services.AddFilesModule(builder.Configuration, builder.Environment); break;
            case "scheduling": builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment); break;
            case "costing": builder.Services.AddCostingModule(builder.Configuration); break;
            case "pricing": builder.Services.AddPricingModule(builder.Configuration); break;
        }
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
        var outbox = context is "costing" or "pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
            : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
        var before = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(long.MaxValue, before.PolicyRevision);
        Assert.Equal(0, before.ControlCapacity.RetainedRecords);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var noOpRequest = new FactCapacityPolicyRequest(Guid.NewGuid(), long.MaxValue, before.MaxRecords,
            before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment");
        var noOp = await policies.AdjustAsync(noOpRequest, "test-operator", now, null);
        Assert.True(noOp.IsSuccess);
        Assert.False(noOp.Value.Changed);
        Assert.Null(noOp.Value.EventId);
        Assert.Equal(long.MaxValue, noOp.Value.PolicyRevision);
        var retained = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(long.MaxValue, retained.PolicyRevision);
        Assert.Equal(before.Business, retained.Business);
        Assert.Equal(1, retained.ControlCapacity.RetainedRecords);
        var changed = noOpRequest with { RequestId = Guid.NewGuid(), MaxRecords = before.MaxRecords + 1 };
        var rejected = await policies.AdjustAsync(changed, "test-operator", now.AddMinutes(1), null);
        Assert.True(rejected.IsFailure);
        Assert.Equal(context + ".audit_policy.conflict", rejected.Error.Code);
        Assert.Equal(retained, (await policies.ReadPolicyAsync()).Value);
        Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(noOp.Value, (await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(1), null)).Value);
        Assert.Equal(retained, (await policies.ReadPolicyAsync()).Value);
    }
}
