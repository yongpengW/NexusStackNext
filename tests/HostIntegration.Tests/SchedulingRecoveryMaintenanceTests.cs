using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Endpoints;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SchedulingRecoveryMaintenanceTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PostgresModule_FailedBackgroundExpiryPreservesReceiptsAndPlanHistory_ThenRecoversInFiniteBatches()
    {
        await using var database = await databases.CreateAsync();
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduling:Storage:Provider"] = "Postgres",
            ["ConnectionStrings:Scheduling"] = database.ConnectionString,
            ["Scheduling:Worker:Enabled"] = "false",
            ["Scheduling:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Scheduling:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("plan-owner"));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var defined = ScheduledTask.Create(new ScheduledTaskId(99002),
            TaskCode.Create("pg-recovery-maintenance-plan").Value, TimeSpan.FromMinutes(1), acceptedAt,
            ScheduleTarget.Create("costing.recalculate", Guid.Parse("11111111-2222-3333-4444-555555555555")).Value, "plan-owner");
        Assert.True(defined.IsSuccess);
        var operationId = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var origin = new ExecutionOrigin(operationId, "scheduling", operationId, "scheduling", "plan-owner", operationId.ToString("N"));
        Assert.True(origin.IsValid());
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        Assert.True((await plans.AddAsync(defined.Value, origin)).IsSuccess);
        var retainedOrigin = await plans.ReadExecutionOriginAsync(defined.Value.Id);
        Assert.NotNull(retainedOrigin);
        Assert.Equal(origin, retainedOrigin);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Triggered);
        var beforePlan = await plans.FindAsync(defined.Value.Id);
        Assert.NotNull(beforePlan);
        var decisions = (await plans.ReadDecisionsAsync(beforePlan.Id.Value, 0, 10)).Items;
        var occurrences = (await plans.ReadOccurrencesAsync(beforePlan.Id.Value, 0, 10)).Items;
        Assert.Single(decisions);
        Assert.Single(occurrences);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(3, originals.Count);
        var trigger = Assert.Single(originals, entry => entry.EventName == ScheduleTriggeredV1.Name);
        var facts = originals.Where(entry => entry.EventName == PlanCommittedV1.Name).ToArray();
        Assert.Equal(2, facts.Length);
        var delivery = scope.ServiceProvider.GetRequiredService<ISchedulingAuditDelivery>();
        var receipts = new List<FactDeliveryRecoveryReceipt>();
        foreach (var original in facts)
        {
            Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-maintenance-stop", acceptedAt, 0));
            var recovered = await delivery.RecoverAsync(new(Guid.NewGuid(), original.Id, acceptedAt, 0, "manual-retry"),
                "maintenance-operator", acceptedAt, null);
            Assert.True(recovered.IsSuccess);
            receipts.Add(recovered.Value);
        }
        var beforeRecovery = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(2, beforeRecovery.Capacity.RetainedRecords);
        var beforeFacts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(trigger, Assert.Single(beforeFacts, entry => entry.Id == trigger.Id));
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("scheduling");
        var beforeBusiness = (await business.ReadAsync()).Value;
        var beforePolicy = (await policies.ReadPolicyAsync()).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var fault = await FactRecoveryMaintenanceFault.InstallAsync(connection, "scheduling");
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            var health = host.Services.GetRequiredService<HealthCheckService>();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var diagnostic = await fault.WaitForFailureAsync(health, budget.Token);
            Assert.True((long)diagnostic.Data["cleanupFailures"] > 0);
            Assert.True((bool)diagnostic.Data["cleanupDegraded"]);
            Assert.Equal(0L, diagnostic.Data["releasedRequests"]);
            Assert.Null(diagnostic.Exception);
            Assert.DoesNotContain("Sensitive controlled maintenance failure", diagnostic.Description!, StringComparison.Ordinal);
            var readiness = await health.CheckHealthAsync(registration => !registration.Tags.Contains("auditing-diagnostics", StringComparer.Ordinal), budget.Token);
            Assert.NotEmpty(readiness.Entries);
            Assert.Equal(HealthStatus.Healthy, readiness.Status);
            foreach (var receipt in receipts)
            { Assert.Equal(receipt, (await delivery.GetRecoveryAsync(receipt.RequestId)).Value); }
            Assert.Equal(beforeRecovery, (await delivery.ReadRecoveryCapacityAsync()).Value);
            await AssertPlanAndFactsAsync();
            await fault.AllowSingleReleaseAsync();
            diagnostic = await fault.WaitForReleaseAsync(health, 2, budget.Token);
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.False((bool)diagnostic.Data["cleanupDegraded"]);
            Assert.True((long)diagnostic.Data["cleanupRuns"] >= 2);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, after.RetainedRecords);
            Assert.Equal(0, after.RetainedPayloadBytes);
            foreach (var receipt in receipts)
            { Assert.Equal("scheduling.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(receipt.RequestId)).Error.Code); }
            await AssertPlanAndFactsAsync();
        }
        finally { await host.StopAsync(); }

        async Task AssertPlanAndFactsAsync()
        {
            Assert.Equal(beforeFacts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(beforePolicy, (await policies.ReadPolicyAsync()).Value);
            Assert.Equal(decisions, (await plans.ReadDecisionsAsync(beforePlan.Id.Value, 0, 10)).Items);
            Assert.Equal(occurrences, (await plans.ReadOccurrencesAsync(beforePlan.Id.Value, 0, 10)).Items);
            Assert.Equal(origin, await plans.ReadExecutionOriginAsync(beforePlan.Id));
            var retained = await plans.FindAsync(beforePlan.Id);
            Assert.NotNull(retained);
            Assert.Equal(beforePlan.Version, retained.Version);
            Assert.Equal(beforePlan.ScheduleRevision, retained.ScheduleRevision);
            Assert.Equal(beforePlan.TriggerSequence, retained.TriggerSequence);
            Assert.Equal(beforePlan.Rule, retained.Rule);
            Assert.Equal(beforePlan.NextRunAt, retained.NextRunAt);
            Assert.Equal(beforePlan.DelegatedBy, retained.DelegatedBy);
            Assert.Equal(beforePlan.UpdatedAt, retained.UpdatedAt);
            Assert.Equal(beforePlan.RetryAt, retained.RetryAt);
            Assert.Equal(beforePlan.IsEnabled, retained.IsEnabled);
        }
    }

    [Fact]
    public async Task MemoryModule_FullRecoveryPoolRejectsAtomically_AndBackgroundExpiryPreservesNonemptyPlanHistory()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(acceptedAt);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduling:Storage:Provider"] = "Memory",
            ["Scheduling:Worker:Enabled"] = "false",
            ["Scheduling:AuditDelivery:Cleanup:Enabled"] = "false",
            ["Scheduling:AuditDelivery:PolicyMaintenance:Enabled"] = "false",
            ["Scheduling:AuditDelivery:MemoryRecoveryControl:MaxRecords"] = "1",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:BatchSize"] = "1",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:Interval"] = "00:00:01",
            ["Scheduling:AuditDelivery:RecoveryMaintenance:Timeout"] = "00:00:01",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser("plan-owner"));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IIntegrationEventSerializer>(new SystemTextJsonIntegrationEventSerializer());
        builder.Services.AddSingleton<IIntegrationEventMapper, NoIntegrationEventsMapper>();
        builder.Services.AddSchedulingModule(builder.Configuration, builder.Environment);
        await using var host = builder.Build();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var initial = await health.CheckHealthAsync(registration => registration.Name == "scheduling-recovery-cleanup");
        Assert.True((bool)Assert.Single(initial.Entries).Value.Data["enabled"]);
        await using var scope = host.Services.CreateAsyncScope();
        var defined = await scope.ServiceProvider.GetRequiredService<TaskRegistry>().DefineAsync(
            TaskCode.Create("recovery-maintenance-plan").Value, TimeSpan.FromMinutes(1),
            ScheduleTarget.Create("costing.recalculate", Guid.Parse("11111111-2222-3333-4444-555555555555")).Value, "plan-owner");
        Assert.True(defined.IsSuccess);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Triggered);
        var plans = scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>();
        var plan = await plans.FindAsync(defined.Value.Id);
        Assert.NotNull(plan);
        var decisions = (await plans.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items;
        Assert.Single(decisions);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var originals = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Equal(3, originals.Count);
        foreach (var original in originals)
        { Assert.True(await outbox.MarkDeadLetteredAsync(original.Id, "controlled-maintenance-stop", acceptedAt, 0)); }
        var occurrences = (await plans.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items;
        Assert.Single(occurrences);
        var facts = originals.Where(entry => entry.EventName == PlanCommittedV1.Name).ToArray();
        Assert.Equal(2, facts.Length);
        var delivery = scope.ServiceProvider.GetRequiredService<ISchedulingAuditDelivery>();
        var request = new FactDeliveryRecoveryRequest(Guid.NewGuid(), facts[0].Id, acceptedAt, 0, "manual-retry");
        Assert.True((await delivery.RecoverAsync(request, "maintenance-operator", acceptedAt, null)).IsSuccess);
        var counted = (await delivery.ReadRecoveryCapacityAsync()).Value;
        Assert.Equal(1, counted.Capacity.MaxRecords);
        Assert.Equal(1, counted.Capacity.RetainedRecords);
        var another = new FactDeliveryRecoveryRequest(Guid.NewGuid(), facts[1].Id, acceptedAt, 0, "manual-retry");
        Assert.Equal("scheduling.delivery_recovery.exhausted", (await delivery.RecoverAsync(another, "maintenance-operator", acceptedAt, null)).Error.Code);
        Assert.Equal(counted, (await delivery.ReadRecoveryCapacityAsync()).Value);
        Assert.True((await delivery.GetRecoveryAsync(another.RequestId)).IsFailure);
        var beforeFacts = await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue);
        Assert.Single(beforeFacts);
        var business = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("scheduling");
        var beforeBusiness = (await business.ReadAsync()).Value;
        clock.Advance(TimeSpan.FromDays(7));
        await host.StartAsync();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            HealthReportEntry diagnostic;
            while (true)
            {
                var report = await health.CheckHealthAsync(registration => registration.Name == "scheduling-recovery-cleanup", budget.Token);
                diagnostic = Assert.Single(report.Entries).Value;
                if ((long)diagnostic.Data["releasedRequests"] == 1) { break; }
                await Task.Delay(50, budget.Token);
            }
            Assert.Equal(HealthStatus.Healthy, diagnostic.Status);
            Assert.Equal(0L, diagnostic.Data["cleanupFailures"]);
            var after = (await delivery.ReadRecoveryCapacityAsync()).Value.Capacity;
            Assert.Equal(0, after.RetainedRecords);
            Assert.Equal(0, after.RetainedPayloadBytes);
            Assert.Equal("scheduling.delivery_recovery.not_found", (await delivery.GetRecoveryAsync(request.RequestId)).Error.Code);
            Assert.Equal(beforeFacts, await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue));
            Assert.Equal(beforeBusiness, (await business.ReadAsync()).Value);
            Assert.Equal(decisions, (await plans.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Items);
            Assert.Equal(occurrences, (await plans.ReadOccurrencesAsync(plan.Id.Value, 0, 10)).Items);
            var retained = await plans.FindAsync(plan.Id);
            Assert.NotNull(retained);
            Assert.Equal(plan.Version, retained.Version);
            Assert.Equal(plan.ScheduleRevision, retained.ScheduleRevision);
            Assert.Equal(plan.TriggerSequence, retained.TriggerSequence);
            Assert.Equal(plan.Rule, retained.Rule);
            Assert.Equal(plan.NextRunAt, retained.NextRunAt);
            Assert.Equal(plan.DelegatedBy, retained.DelegatedBy);
            Assert.True((await delivery.RecoverAsync(another, "maintenance-operator", clock.UtcNow, null)).IsSuccess);
        }
        finally { await host.StopAsync(); }
    }
}
