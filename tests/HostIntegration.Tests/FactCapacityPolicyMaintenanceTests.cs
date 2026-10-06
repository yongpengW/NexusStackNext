using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
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

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyMaintenanceTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("Identity")]
    [InlineData("Files")]
    [InlineData("Scheduling")]
    [InlineData("Platform")]
    public async Task MemoryModule_BackgroundMaintenanceReleasesExpiredNoOpsInFiniteBatches(string context)
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateMemoryModule(context, acceptedAt.AddDays(7));
        var contextKey = context.ToLowerInvariant();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var initialHealth = await health.CheckHealthAsync(registration => registration.Name == contextKey + "-policy-cleanup");
        Assert.True((bool)Assert.Single(initialHealth.Entries).Value.Data["enabled"]);
        await using var scope = host.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(contextKey);
        var before = (await policies.ReadPolicyAsync()).Value;
        for (var index = 0; index < 3; index++)
        {
            var receipt = await policies.AdjustAsync(new(Guid.NewGuid(), before.PolicyRevision, before.MaxRecords,
                before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment"), "test-operator", acceptedAt, null);
            Assert.True(receipt.IsSuccess);
            Assert.False(receipt.Value.Changed);
        }
        Assert.Equal(3, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        await host.StartAsync();
        try
        {
            var diagnostic = await WaitForReleasedAsync(health, contextKey, 3);
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 3);
            Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(0, after.ControlCapacity.RetainedRecords);
            Assert.Equal(0, after.ControlCapacity.RetainedPayloadBytes);
            Assert.Equal(before.PolicyRevision, after.PolicyRevision);
            Assert.Equal(before.Business, after.Business);
            Assert.Empty(await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(contextKey)
                .ReadPendingAsync(10, DateTimeOffset.MaxValue));
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public Task IdentityPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Identity");

    [PostgresFact]
    public Task FilesPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Files");

    [PostgresFact]
    public Task SchedulingPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Scheduling");

    [PostgresFact]
    public Task PlatformPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Platform");

    [PostgresFact]
    public Task CostingPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Costing");

    [PostgresFact]
    public Task PricingPostgresPolicyMaintenance_ExposesRollbackFailureThenReleasesBatchesAfterRecovery()
        => VerifyPostgresMaintenanceAsync("Pricing");

    [Theory]
    [InlineData("Identity")]
    [InlineData("Files")]
    [InlineData("Scheduling")]
    [InlineData("Platform")]
    public async Task DisabledMaintenance_ReportsItsStateAndKeepsExpiredReceipts(string context)
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(context, acceptedAt.AddDays(7), overrides: new()
        {
            [$"{context}:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
        });
        var contextKey = context.ToLowerInvariant();
        await using var scope = host.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(contextKey);
        var before = (await policies.ReadPolicyAsync()).Value;
        var receipt = await policies.AdjustAsync(new(Guid.NewGuid(), before.PolicyRevision, before.MaxRecords,
            before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment"), "test-operator", acceptedAt, null);
        Assert.True(receipt.IsSuccess);
        await host.StartAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.1));
            var health = host.Services.GetRequiredService<HealthCheckService>();
            var report = await health.CheckHealthAsync(registration => registration.Name == contextKey + "-policy-cleanup");
            var diagnostic = Assert.Single(report.Entries).Value;
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["enabled"]);
            Assert.Equal(0L, diagnostic.Data["cleanupRuns"]);
            Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(1, after.ControlCapacity.RetainedRecords);
            Assert.Equal(before.PolicyRevision, after.PolicyRevision);
            Assert.Equal(before.Business, after.Business);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("Interval", "00:00:00.9999999")]
    [InlineData("Interval", "01:00:00.0000001")]
    [InlineData("Timeout", "00:00:00.0499999")]
    [InlineData("Timeout", "00:00:30.0000001")]
    public void InvalidMaintenanceLimits_AreRejectedEvenWhenDisabled(string option, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CreateModule("Identity", DateTimeOffset.UtcNow, overrides: new()
        {
            ["Identity:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Identity:AuditDelivery:PolicyMaintenance:" + option] = value,
        }));
        Assert.Equal("容量策略凭据维护配置超出允许范围。", error.Message);
    }

    [PostgresFact]
    public Task CleanupBudgetExpiry_RollsBackWaitingWorkAndRecoversNextRound() => VerifyWaitingCleanupAsync(false);

    [PostgresFact]
    public Task HostShutdown_CancelsWaitingCleanupWithoutDeletingReceiptOrReportingFailure() => VerifyWaitingCleanupAsync(true);

    private async Task VerifyWaitingCleanupAsync(bool shutdown)
    {
        await using var database = await databases.CreateAsync();
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        // The held row forces expiry; the recovery round must retain a usable production budget.
        var maintenanceTimeout = shutdown ? TimeSpan.FromSeconds(30) : new FactCapacityPolicyMaintenanceOptions().Timeout;
        await using var host = CreateModule("Identity", acceptedAt.AddDays(7), database.ConnectionString, new()
        {
            ["Identity:AuditDelivery:CapacityRead:Timeout"] = "00:00:30",
            ["Identity:AuditDelivery:PolicyMaintenance:Timeout"] = maintenanceTimeout.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        });
        await using var scope = host.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("identity");
        var before = (await policies.ReadPolicyAsync()).Value;
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), before.PolicyRevision, before.MaxRecords,
            before.MaxPayloadBytes, before.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(request, "test-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var hold = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT \"Id\" FROM identity.fact_policy_control WHERE \"Id\" = 1 FOR UPDATE", connection, hold))
        {
            Assert.Equal(1, await acquire.ExecuteScalarAsync());
        }
        await host.StartAsync();
        try
        {
            var health = host.Services.GetRequiredService<HealthCheckService>();
            {
                using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database
                        AND wait_event_type = 'Lock' AND query LIKE '%fact_policy_control%')
                    """, connection, hold);
                waiting.Parameters.AddWithValue("database", connection.Database);
                await using var refresh = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", connection, hold);
                while (true)
                {
                    await refresh.ExecuteNonQueryAsync(observe.Token);
                    if ((bool)(await waiting.ExecuteScalarAsync(observe.Token))!) { break; }
                    await Task.Delay(10, observe.Token);
                }
            }
            if (shutdown)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await host.StopAsync(stop.Token);
                Assert.False(stop.IsCancellationRequested);
                var stopped = await health.CheckHealthAsync(registration => registration.Name == "identity-policy-cleanup");
                var diagnostic = Assert.Single(stopped.Entries).Value;
                Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
                Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
                Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
            }
            else
            {
                using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                HealthReportEntry diagnostic;
                do
                {
                    var report = await health.CheckHealthAsync(registration => registration.Name == "identity-policy-cleanup", observe.Token);
                    diagnostic = Assert.Single(report.Entries).Value;
                    if (diagnostic.Status != HealthStatus.Degraded) { await Task.Delay(10, observe.Token); }
                } while (diagnostic.Status != HealthStatus.Degraded);
                Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
                Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
            }
            var retained = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(1, retained.ControlCapacity.RetainedRecords);
            Assert.Equal(before.PolicyRevision, retained.PolicyRevision);
            Assert.Equal(before.Business, retained.Business);
            await hold.RollbackAsync();
            if (shutdown)
            {
                Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", acceptedAt.AddDays(7), null)).Value);
            }
            else
            {
                var recovered = await WaitForReleasedAsync(health, "identity", 1);
                Assert.Equal(HealthStatus.Healthy, recovered.Status);
                Assert.False((bool)recovered.Data["cleanupDegraded"]);
                var released = (await policies.ReadPolicyAsync()).Value;
                Assert.Equal(0, released.ControlCapacity.RetainedRecords);
                Assert.Equal(before.Business, released.Business);
            }
        }
        finally { await host.StopAsync(); }
    }
    private async Task VerifyPostgresMaintenanceAsync(string context)
    {
        await using var database = await databases.CreateAsync(context);
        var contextKey = context.ToLowerInvariant();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = connection.CreateCommand())
        {
            fault.CommandText = $"""
                CREATE FUNCTION {contextKey}.reject_policy_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."EventName" = '{contextKey}.fact-capacity-policy-changed.v1' THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Controlled policy cleanup failure.';
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER reject_policy_cleanup BEFORE DELETE ON {contextKey}.outbox
                    FOR EACH ROW EXECUTE FUNCTION {contextKey}.reject_policy_cleanup();
                """;
            await fault.ExecuteNonQueryAsync();
        }
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        await using var host = CreateModule(context, now.AddDays(7), database.ConnectionString);
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>(contextKey);
            var outbox = context is "Costing" or "Pricing" ? scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                : scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(contextKey);
            var request = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                1, 2, 16384, 16384, "operator-adjustment");
            var accepted = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(accepted.IsSuccess);
            Assert.NotNull(accepted.Value.EventId);
            await outbox.MarkDeliveredAsync(accepted.Value.EventId.Value, now);
            var noOpRequest = request with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000002"), ExpectedPolicyRevision = 2 };
            var noOp = await policies.AdjustAsync(noOpRequest, "test-operator", now, null);
            Assert.True(noOp.IsSuccess);
            var pendingRequest = request with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000003"), ExpectedPolicyRevision = 2, MaxRecords = 3 };
            var pending = await policies.AdjustAsync(pendingRequest, "test-operator", now, null);
            Assert.True(pending.IsSuccess);
            var deadRequest = request with { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000004"), ExpectedPolicyRevision = 3, MaxRecords = 4 };
            var dead = await policies.AdjustAsync(deadRequest, "test-operator", now, null);
            Assert.True(dead.IsSuccess);
            Assert.True(await outbox.MarkDeadLetteredAsync(dead.Value.EventId!.Value, "test-failure", now, 0));
            var before = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(4, before.ControlCapacity.RetainedRecords);
            var health = host.Services.GetRequiredService<HealthCheckService>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == contextKey + "-policy-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if (diagnostic.Status != HealthStatus.Degraded) { await Task.Delay(50, budget.Token); }
            } while (diagnostic.Status != HealthStatus.Degraded);
            Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
            Assert.DoesNotContain("Controlled policy cleanup failure", diagnostic.Description!, StringComparison.Ordinal);
            var readiness = await health.CheckHealthAsync(registration => !registration.Tags.Contains("auditing-diagnostics", StringComparer.Ordinal), budget.Token);
            Assert.NotEmpty(readiness.Entries);
            Assert.Equal(HealthStatus.Healthy, readiness.Status);
            Assert.Equal(before, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(accepted.Value, (await policies.AdjustAsync(request, "test-operator", now.AddDays(7), null)).Value);
            Assert.Equal(noOp.Value, (await policies.AdjustAsync(noOpRequest, "test-operator", now.AddDays(7), null)).Value);
            await using (var recover = connection.CreateCommand())
            {
                recover.CommandText = $"DROP TRIGGER reject_policy_cleanup ON {contextKey}.outbox";
                await recover.ExecuteNonQueryAsync();
            }
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == contextKey + "-policy-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] != 2) { await Task.Delay(50, budget.Token); }
            } while ((long)diagnostic.Data["releasedRequests"] != 2);
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
            var after = (await policies.ReadPolicyAsync()).Value;
            Assert.Equal(2, after.ControlCapacity.RetainedRecords);
            Assert.True(after.ControlCapacity.RetainedPayloadBytes > 0);
            Assert.Equal(before.PolicyRevision, after.PolicyRevision);
            Assert.Equal(before.Business, after.Business);
            Assert.Equal(pending.Value, (await policies.AdjustAsync(pendingRequest, "test-operator", now.AddDays(30), null)).Value);
            Assert.Equal(dead.Value, (await policies.AdjustAsync(deadRequest, "test-operator", now.AddDays(30), null)).Value);
            Assert.Equal(pending.Value.EventId, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).Id);
        }
        finally { await host.StopAsync(); }
    }

    private static WebApplication CreateMemoryModule(string context, DateTimeOffset now) => CreateModule(context, now);

    private static WebApplication CreateModule(string context, DateTimeOffset now, string? connection = null,
        Dictionary<string, string?>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "policy-maintenance-test-signing-key-long-enough-for-hs256",
            [$"{context}:Storage:Provider"] = connection is null ? "Memory" : "Postgres",
            [$"ConnectionStrings:{context}"] = connection,
            [$"{context}:Messaging:Enabled"] = "false",
            [$"{context}:Worker:Enabled"] = "false",
            [$"{context}:AuditDelivery:Cleanup:Enabled"] = "false",
            [$"{context}:AuditDelivery:PolicyMaintenance:BatchSize"] = "1",
            [$"{context}:AuditDelivery:PolicyMaintenance:Interval"] = "00:00:01",
            [$"{context}:AuditDelivery:PolicyMaintenance:Timeout"] = "00:00:01",
            ["Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "nsn-policy-maintenance-" + Guid.NewGuid().ToString("N")),
        });
        if (overrides is not null) { builder.Configuration.AddInMemoryCollection(overrides); }
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(new FixedClock(now));
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        switch (context)
        {
            case "Identity": builder.Services.AddIdentityModule(builder.Configuration, builder.Environment); break;
            case "Files": builder.Services.AddFilesModule(builder.Configuration, builder.Environment); break;
            case "Scheduling": builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment); break;
            case "Platform": builder.Services.AddPlatformModule(builder.Configuration, builder.Environment); break;
            case "Costing": builder.Services.AddCostingModule(builder.Configuration); break;
            case "Pricing": builder.Services.AddPricingModule(builder.Configuration); break;
            default: throw new ArgumentException("Unknown test module.", nameof(context));
        }
        return builder.Build();
    }
    private static async Task<HealthReportEntry> WaitForReleasedAsync(HealthCheckService health, string context, long released)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var report = await health.CheckHealthAsync(registration => registration.Name == context + "-policy-cleanup", budget.Token);
            var entry = Assert.Single(report.Entries).Value;
            if ((long)entry.Data["releasedRequests"] == released) { return entry; }
            await Task.Delay(50, budget.Token);
        }
    }
}
