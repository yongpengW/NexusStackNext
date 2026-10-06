using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactDeliveryRecoveryMaintenanceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresModule_BackgroundCleanupRollsBackFailure_ReportsSafeDegradation_AndRecoversNextRound()
    {
        await using var database = await databases.CreateAsync();
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Storage:Provider"] = "Postgres",
            ["ConnectionStrings:Platform"] = database.ConnectionString,
            ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Platform:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Platform:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Platform:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddPlatformModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("maintenance.rollback").Value, "retained")).IsSuccess);
        var original = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), clock, new() { MaxAttempts = 1 });
        Assert.Equal(1, (await publisher.PublishPendingAsync()).DeadLettered);
        var accepted = await delivery.RecoverAsync(new(Guid.NewGuid(), original.Id, acceptedAt, 0, "manual-retry"),
            "maintenance-operator", acceptedAt, null);
        Assert.True(accepted.IsSuccess);
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforeFact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = new NpgsqlCommand("""
            CREATE FUNCTION platform.reject_maintained_recovery_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RetainedRecords" < OLD."RetainedRecords" THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Sensitive controlled maintenance failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_maintained_recovery_release BEFORE UPDATE ON platform.fact_recovery_control
                FOR EACH ROW EXECUTE FUNCTION platform.reject_maintained_recovery_release();
            """, connection))
        { await fault.ExecuteNonQueryAsync(); }
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            var health = host.Services.GetRequiredService<HealthCheckService>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "platform-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if (diagnostic.Status == HealthStatus.Degraded) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
            Assert.True((bool)diagnostic.Data["cleanupDegraded"]);
            Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
            Assert.Null(diagnostic.Exception);
            Assert.DoesNotContain("Sensitive controlled maintenance failure", diagnostic.Description!, StringComparison.Ordinal);
            var readiness = await health.CheckHealthAsync(registration => !registration.Tags.Contains("auditing-diagnostics", StringComparer.Ordinal), budget.Token);
            Assert.NotEmpty(readiness.Entries);
            Assert.Equal(HealthStatus.Healthy, readiness.Status);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(accepted.Value.RequestId)).Value);
            Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
            await using (var repair = new NpgsqlCommand("""
                DROP TRIGGER reject_maintained_recovery_release ON platform.fact_recovery_control;
                DROP FUNCTION platform.reject_maintained_recovery_release();
                """, connection))
            { await repair.ExecuteNonQueryAsync(); }
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "platform-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 1) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, after.RetainedRecords);
            Assert.Equal(0, after.RetainedPayloadBytes);
            Assert.Equal("platform.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(accepted.Value.RequestId)).Error.Code);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task MemoryModule_BackgroundMaintenanceReleasesExpiredReceiptsInFiniteBatches()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Storage:Provider"] = "Memory",
            ["Platform:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Platform:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Platform:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Platform:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Platform:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddPlatformModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var initial = await health.CheckHealthAsync(registration => registration.Name == "platform-recovery-cleanup");
        Assert.True((bool)Assert.Single(initial.Entries).Value.Data["enabled"]);
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingStore>();
        var delivery = scope.ServiceProvider.GetRequiredService<ISettingAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("platform");
        Assert.True((await settings.WriteAsync(SettingKey.Create("maintenance.first").Value, "first")).IsSuccess);
        Assert.True((await settings.WriteAsync(SettingKey.Create("maintenance.second").Value, "second")).IsSuccess);
        var originals = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), clock, new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        foreach (var original in originals)
        {
            Assert.True((await delivery.RecoverAsync(new(Guid.NewGuid(), original.Id, acceptedAt, 0, "manual-retry"),
                "maintenance-operator", acceptedAt, null)).IsSuccess);
        }
        Assert.Equal(2, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        var pendingBefore = (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray();
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "platform-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 2) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
            Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value;
            Assert.Equal(0, after.Capacity.RetainedRecords);
            Assert.Equal(0, after.Capacity.RetainedPayloadBytes);
            Assert.Equal(pendingBefore, (await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)).ToArray());
        }
        finally { await host.StopAsync(); }
    }
}
