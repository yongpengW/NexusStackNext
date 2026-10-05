using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Identity.Application;
using NexusStackNext.Identity.Endpoints;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class IdentityRecoveryMaintenanceTests
{
    [PostgresFact]
    public async Task PostgresModule_BackgroundCleanupRollsBackFailure_ReportsSafeDegradation_AndRecoversNextRound()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "recovery-maintenance-test-signing-key-long-enough-for-hs256",
            ["Identity:Storage:Provider"] = "Postgres",
            ["ConnectionStrings:Identity"] = database.ConnectionString,
            ["Identity:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Identity:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Identity:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Identity:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Identity:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        Assert.True((await sender.SendAsync(new CreateUserCommand("maintenance-rollback", "maintenance-password"))).IsSuccess);
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
            CREATE FUNCTION identity.reject_maintained_recovery_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RetainedRecords" < OLD."RetainedRecords" THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Sensitive controlled maintenance failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_maintained_recovery_release BEFORE UPDATE ON identity.fact_recovery_control
                FOR EACH ROW EXECUTE FUNCTION identity.reject_maintained_recovery_release();
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
                var report = await health.CheckHealthAsync(registration => registration.Name == "identity-recovery-cleanup", budget.Token);
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
                DROP TRIGGER reject_maintained_recovery_release ON identity.fact_recovery_control;
                DROP FUNCTION identity.reject_maintained_recovery_release();
                """, connection))
            { await repair.ExecuteNonQueryAsync(); }
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "identity-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 1) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, after.RetainedRecords);
            Assert.Equal(0, after.RetainedPayloadBytes);
            Assert.Equal("identity.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(accepted.Value.RequestId)).Error.Code);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue)));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task MemoryModule_BackgroundMaintenanceReleasesExpiredReceiptsInFiniteBatches_WithoutChangingFacts()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "recovery-maintenance-test-signing-key-long-enough-for-hs256",
            ["Identity:Storage:Provider"] = "Memory",
            ["Identity:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Identity:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Identity:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Identity:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Identity:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(null));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var initial = await health.CheckHealthAsync(registration => registration.Name == "identity-recovery-cleanup");
        Assert.True((bool)Assert.Single(initial.Entries).Value.Data["enabled"]);
        await using var scope = host.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await sender.SendAsync(new CreateUserCommand("maintenance-first", "maintenance-password"))).IsSuccess);
        Assert.True((await sender.SendAsync(new CreateUserCommand("maintenance-second", "maintenance-password"))).IsSuccess);
        var delivery = scope.ServiceProvider.GetRequiredService<IIdentityAuditDelivery>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("identity");
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("identity");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        foreach (var original in originals)
        {
            Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-maintenance-stop", acceptedAt, 0));
            Assert.True((await delivery.RecoverAsync(new(Guid.NewGuid(), original.Id, acceptedAt, 0, "manual-retry"),
                "maintenance-operator", acceptedAt, null)).IsSuccess);
        }
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforeFacts = (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).ToArray();
        Assert.Equal(2, (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity.RetainedRecords);
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "identity-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 2) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
            Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, after.RetainedRecords);
            Assert.Equal(0, after.RetainedPayloadBytes);
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforeFacts, (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).ToArray());
        }
        finally { await host.StopAsync(); }
    }
}
