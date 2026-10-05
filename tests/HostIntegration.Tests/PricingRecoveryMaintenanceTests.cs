using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PricingRecoveryMaintenanceTests
{
    [PostgresFact]
    public async Task Module_ReportsRecoveryCleanupFailure_ThenReleasesExpiredEvidenceWithoutChangingCompletedPricingWork()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
            ["ConnectionStrings:Pricing"] = database.ConnectionString,
            ["Pricing:Worker:Enabled"] = "false",
            ["Pricing:Messaging:Enabled"] = "false",
            ["Pricing:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Pricing:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Pricing:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Pricing:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Pricing:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "pricing");
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("cost-owner"));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddPricingModule(builder.Configuration);
        await using var host = builder.Build();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var initial = await health.CheckHealthAsync(registration => registration.Name == "pricing-recovery-cleanup");
        Assert.True((bool)Assert.Single(initial.Entries).Value.Data["enabled"]);
        await using var scope = host.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var inputs = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Assert.True((await sender.SendAsync(inputs)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value;
        Assert.NotNull(lease);
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        var cost = (await sender.QueryAsync(new GetPriceQuote(inputs.ItemId))).Value;
        Assert.Equal(100m, cost.BreakEvenPrice);
        var task = (await sender.QueryAsync(new GetRecalculation(inputs.RequestId))).Value;
        Assert.Single(task.History);
        var beforeTask = JsonSerializer.Serialize(task);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(2, originals.Count);
        var publisher = new OutboxPublisher(outbox, new FailingEventBus(), new FixedClock(now), new() { MaxAttempts = 1 });
        Assert.Equal(2, (await publisher.PublishPendingAsync()).DeadLettered);
        var delivery = scope.ServiceProvider.GetRequiredService<IPricingAuditDelivery>();
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), originals.Last(entry => entry.EventName == PriceQuoteCommittedV1.Name).Id,
            now, 0, "manual-retry");
        var accepted = await delivery.RecoverAsync(request, "recovery-operator", now, null);
        Assert.True(accepted.IsSuccess);
        var beforeCapacity = (await delivery.ReadRecoveryCapacityAsync()).Value;
        var beforeFact = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("pricing");
        var beforePolicies = (await policies.ReadPolicyAsync()).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var blocked = await connection.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM pricing.fact_recovery_control WHERE \"Id\" = 1 FOR UPDATE", connection, blocked))
        { Assert.Equal(1, await hold.ExecuteScalarAsync()); }
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            HealthReportEntry diagnostic;
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "pricing-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["cleanupFailures"] == 0) { await Task.Delay(50, budget.Token); }
            } while ((long)diagnostic.Data["cleanupFailures"] == 0);
            Assert.Equal(HealthStatus.Degraded, diagnostic.Status);
            Assert.Equal(beforeCapacity, (await delivery.ReadRecoveryCapacityAsync()).Value);
            Assert.Equal(accepted.Value, (await delivery.GetRecoveryAsync(request.RequestId)).Value);
            await blocked.CommitAsync();
            do
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "pricing-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 0) { await Task.Delay(50, budget.Token); }
            } while ((long)diagnostic.Data["releasedRequests"] == 0);
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.Equal(1L, diagnostic.Data["releasedRequests"]);
            var released = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, released.RetainedRecords);
            Assert.Equal(0, released.RetainedPayloadBytes);
            Assert.Equal("pricing.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            Assert.Equal(beforeFact, Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)));
            Assert.Equal(beforePolicies, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(cost, (await sender.QueryAsync(new GetPriceQuote(inputs.ItemId))).Value);
            Assert.Equal(beforeTask, JsonSerializer.Serialize((await sender.QueryAsync(new GetRecalculation(inputs.RequestId))).Value));
        }
        finally { await host.StopAsync(); }
    }
}
