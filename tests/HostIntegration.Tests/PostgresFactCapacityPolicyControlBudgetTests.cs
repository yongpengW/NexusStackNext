using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
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
using NpgsqlTypes;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PostgresFactCapacityPolicyControlBudgetTests
{
    [PostgresFact]
    public Task PlatformPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("platform", "Platform");
    [PostgresFact]
    public Task IdentityPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("identity", "Identity");
    [PostgresFact]
    public Task FilesPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("files", "Files");
    [PostgresFact]
    public Task SchedulingPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("scheduling", "Scheduling");
    [PostgresFact]
    public Task CostingPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("costing", "Costing");
    [PostgresFact]
    public Task PricingPostgres_UnicodeControlFact_UsesUtf8SingleLimitAndRejectsWithoutPartialState()
        => AssertUtf8SingleLimitAsync("pricing", "Pricing");

    private static async Task AssertUtf8SingleLimitAsync(string context, string configurationContext)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAsync(database, configurationContext);
        await ArrangeControlLimitsAsync(database.ConnectionString, context, 16 * 1024 * 1024, 2800);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var serializer = new SystemTextJsonIntegrationEventSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await using var host = CreateModule(configurationContext, database.ConnectionString, now, serializer);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
            var outbox = context is "costing" or "pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
            var initial = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2800, initial.ControlCapacity.MaxRecordPayloadBytes);
            Assert.Equal(16 * 1024 * 1024, initial.ControlCapacity.MaxPayloadBytes);
            var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000003"), 1,
                initial.MaxRecords + 1, initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
            var rejected = await policies.AdjustAsync(request, new string('界', 200), now, null);
            Assert.True(rejected.IsFailure);
            Assert.Equal($"{context}.audit_policy.control_exhausted", rejected.Error.Code);
            Assert.Equal(initial, (await policies.ReadPolicyAsync()).Value);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            var retry = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(retry.IsSuccess);
            Assert.True(retry.Value.Changed);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2, after.PolicyRevision);
            Assert.Equal(initial.MaxRecords + 1, after.MaxRecords);
            Assert.Equal(initial.RetainedRecords, after.RetainedRecords);
            Assert.Equal(initial.RetainedPayloadBytes, after.RetainedPayloadBytes);
            Assert.Equal(1, after.ControlCapacity.RetainedRecords);
            Assert.InRange(after.ControlCapacity.RetainedPayloadBytes, 1, 2800);
            Assert.Equal(retry.Value.EventId, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public Task PlatformPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("platform", "Platform");
    [PostgresFact]
    public Task IdentityPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("identity", "Identity");
    [PostgresFact]
    public Task FilesPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("files", "Files");
    [PostgresFact]
    public Task SchedulingPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("scheduling", "Scheduling");
    [PostgresFact]
    public Task CostingPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("costing", "Costing");
    [PostgresFact]
    public Task PricingPostgres_PersistedTotalByteLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("pricing", "Pricing");

    [PostgresFact]
    public Task PlatformPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("platform", "Platform", recordsLimited: true);
    [PostgresFact]
    public Task IdentityPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("identity", "Identity", recordsLimited: true);
    [PostgresFact]
    public Task FilesPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("files", "Files", recordsLimited: true);
    [PostgresFact]
    public Task SchedulingPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("scheduling", "Scheduling", recordsLimited: true);
    [PostgresFact]
    public Task CostingPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("costing", "Costing", recordsLimited: true);
    [PostgresFact]
    public Task PricingPostgres_PersistedRecordLimit_RejectsAtomicallyAndReleasesForRetry()
        => AssertTotalByteLimitAsync("pricing", "Pricing", recordsLimited: true);

    private static async Task AssertTotalByteLimitAsync(string context, string configurationContext, bool recordsLimited = false)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await MigrateAsync(database, configurationContext);
        await ArrangeControlLimitsAsync(database.ConnectionString, context, recordsLimited ? 16 * 1024 * 1024 : 1024,
            recordsLimited ? 16 * 1024 : 1024, recordsLimited ? 1 : null);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(configurationContext, database.ConnectionString, now);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(context);
            var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>(context);
            var outbox = context is "costing" or "pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(context);
            var initial = (await policies.ReadPolicyAsync()).Value;
            Assert.True(initial.IsPersistent);
            Assert.Equal(recordsLimited ? 1 : 1000, initial.ControlCapacity.MaxRecords);
            Assert.Equal(recordsLimited ? 16 * 1024 * 1024 : 1024, initial.ControlCapacity.MaxPayloadBytes);
            Assert.Equal(recordsLimited ? 16 * 1024 : 1024, initial.ControlCapacity.MaxRecordPayloadBytes);
            var first = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1,
                initial.MaxRecords, initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
            var accepted = await policies.AdjustAsync(first, "test-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.False(accepted.Value.Changed);
            var retained = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(1, retained.ControlCapacity.RetainedRecords);
            if (recordsLimited)
            {
                Assert.InRange(retained.ControlCapacity.RetainedPayloadBytes, 1, 1024);
                Assert.Equal(retained.ControlCapacity.MaxRecords, retained.ControlCapacity.RetainedRecords);
            }
            else
            {
                Assert.InRange(retained.ControlCapacity.RetainedPayloadBytes, 513, 1024);
                Assert.True(retained.ControlCapacity.RetainedRecords < retained.ControlCapacity.MaxRecords);
            }
            var second = first with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
            var rejected = await policies.AdjustAsync(second, "test-operator", now, null);
            Assert.True(rejected.IsFailure);
            Assert.Equal($"{context}.audit_policy.control_exhausted", rejected.Error.Code);
            Assert.Equal(retained, (await policies.ReadPolicyAsync()).Value);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
            Assert.Equal(accepted.Value, (await policies.AdjustAsync(first, "test-operator", now.AddDays(1), null)).Value);
            Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(7)));
            var released = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(0, released.ControlCapacity.RetainedRecords);
            Assert.Equal(0, released.ControlCapacity.RetainedPayloadBytes);
            Assert.Equal(1, released.PolicyRevision);
            Assert.Equal(initial.Business, released.Business);
            var retried = await policies.AdjustAsync(second, "test-operator", now.AddDays(7), null);
            Assert.True(retried.IsSuccess);
            Assert.False(retried.Value.Changed);
            Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
            Assert.Empty(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        }
        finally { await host.StopAsync(); }
    }

    private static async Task ArrangeControlLimitsAsync(string connectionString, string context, long maxBytes, int maxSingleBytes,
        long? maxRecords = null)
    {
        if (context is not ("platform" or "identity" or "files" or "scheduling" or "costing" or "pricing"))
        { throw new ArgumentException("Unknown owned test schema.", nameof(context)); }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // Fixture arrangement only: assertions use the actual module's public policy, cleanup and Outbox ports.
        await using var command = new NpgsqlCommand($"""
            UPDATE {context}.fact_policy_control SET "MaxPayloadBytes" = @bytes, "MaxRecordPayloadBytes" = @single
                , "MaxRecords" = COALESCE(@records, "MaxRecords")
                WHERE "Id" = 1 AND "RetainedRecords" = 0 AND "RetainedPayloadBytes" = 0
            """, connection);
        command.Parameters.AddWithValue("bytes", maxBytes);
        command.Parameters.AddWithValue("single", maxSingleBytes);
        command.Parameters.Add("records", NpgsqlDbType.Bigint).Value = maxRecords is null ? DBNull.Value : maxRecords.Value;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static WebApplication CreateModule(string context, string connection, DateTimeOffset now,
        IIntegrationEventSerializer? serializer = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "policy-control-test-signing-key-long-enough-for-hs256",
            [$"{context}:Storage:Provider"] = "Postgres",
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Messaging:Enabled"] = "false",
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{context}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            // Deliberately different valid Memory values must not overwrite persisted PostgreSQL limits.
            [$"{context}:AuditDelivery:MemoryPolicyControl:MaxRecords"] = "1",
            [$"{context}:AuditDelivery:MemoryPolicyControl:MaxPayloadBytes"] = "256",
            [$"{context}:AuditDelivery:MemoryPolicyControl:MaxRecordPayloadBytes"] = "256",
            ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-control-" + Guid.NewGuid().ToString("N")),
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddSingleton(serializer ?? new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        switch (context)
        {
            case "Platform": builder.Services.AddPlatformModule(builder.Configuration, builder.Environment); break;
            case "Identity": builder.Services.AddIdentityModule(builder.Configuration, builder.Environment); break;
            case "Files": builder.Services.AddFilesModule(builder.Configuration, builder.Environment); break;
            case "Scheduling": builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment); break;
            case "Costing": builder.Services.AddCostingModule(builder.Configuration); break;
            case "Pricing": builder.Services.AddPricingModule(builder.Configuration); break;
            default: throw new ArgumentException("Unknown test module.", nameof(context));
        }
        return builder.Build();
    }

    private static Task MigrateAsync(IdentityJourneyDatabase database, string context) => context switch
    {
        "Costing" => CostingDatabase.MigrateAsync(database.ConnectionString),
        "Pricing" => PricingDatabase.MigrateAsync(database.ConnectionString),
        _ => database.MigrateAsync(),
    };
}
