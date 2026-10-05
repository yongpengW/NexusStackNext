using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class SchedulingFactCapacityPolicyHistoryTests
{
    [PostgresFact]
    public async Task SchedulingPostgres_PolicyFailuresAndBoundedCleanup_PreserveExistingDecisionAndDeadLetterHistory()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        var serializer = new RejectingEventSerializer(new SystemTextJsonIntegrationEventSerializer());
        await using var app = new PersistentHistoryApp(database.ConnectionString, serializer);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertHistoryIsolationAsync(scope.ServiceProvider, serializer);
    }

    [Fact]
    public async Task SchedulingMemory_PolicyFailuresAndBoundedCleanup_PreserveExistingDecisionAndDeadLetterHistory()
    {
        var serializer = new RejectingEventSerializer(new SystemTextJsonIntegrationEventSerializer());
        await using var baseApp = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        await AssertHistoryIsolationAsync(scope.ServiceProvider, serializer);
    }

    private static async Task AssertHistoryIsolationAsync(IServiceProvider services, RejectingEventSerializer serializer)
    {
        var plans = services.GetRequiredService<IScheduledTaskStore>();
        var policies = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("scheduling");
        var cleanup = services.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>("scheduling");
        var outbox = services.GetRequiredKeyedService<IOutboxStore>("scheduling");
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var plan = ScheduledTask.Create(new ScheduledTaskId(88021), TaskCode.Create("policy-history").Value,
            TimeSpan.FromHours(1), now, ScheduleTarget.Create("costing.recalculate",
                Guid.Parse("00000000-0000-0000-0000-000000000010")).Value, "42").Value;
        Assert.True((await plans.AddAsync(plan)).IsSuccess);
        Assert.True(plan.Advance(now, now.AddHours(1), trigger: true).IsSuccess);
        var occurrenceId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var occurrence = new ScheduleOccurrence(occurrenceId, plan.Id.Value, plan.TriggerSequence, now, now,
            plan.Target.Kind, plan.Target.SubjectId, plan.DelegatedBy);
        var decision = new ScheduleDecision(occurrenceId, plan.Id.Value, plan.Version, plan.ScheduleRevision, plan.Rule,
            "Triggered", now, now, now.AddHours(1), occurrenceId);
        Assert.True((await plans.RecordDecisionAsync(plan, 1, decision, occurrence)).IsSuccess);
        Assert.True(await outbox.MarkFailedAsync(occurrenceId, "test.delivery_failure", now.AddMinutes(1), 0));
        Assert.True(await outbox.MarkDeadLetteredAsync(occurrenceId, "test.delivery_stopped", now.AddMinutes(2), 0));
        var decisions = await plans.ReadDecisionsAsync(plan.Id.Value, 0, 10);
        Assert.Equal(1, decisions.Total);
        Assert.Equal(decision, Assert.Single(decisions.Items));
        var occurrences = await plans.ReadOccurrencesAsync(plan.Id.Value, 0, 10);
        Assert.Equal(1, occurrences.Total);
        var stopped = Assert.Single(occurrences.Items);
        Assert.Equal("DeadLettered", stopped.DeliveryState);
        Assert.Equal(2, stopped.AttemptCount);
        Assert.NotNull(stopped.DeadLetteredAt);
        var planJson = JsonSerializer.Serialize(await plans.FindAsync(plan.Id));
        var decisionJson = JsonSerializer.Serialize(decisions);
        var occurrenceJson = JsonSerializer.Serialize(occurrences);
        var business = await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue);
        Assert.Equal(2, business.Count);
        Assert.All(business, entry => Assert.Equal(PlanCommittedV1.Name, entry.EventName));
        var initial = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, initial.RetainedRecords);
        var first = new FactCapacityPolicyRequest(Guid.Parse("00000000-0000-0000-0000-000000000012"), 1,
            initial.MaxRecords + 1, initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var accepted = await policies.AdjustAsync(first, "test-operator", now, null);
        Assert.True(accepted.IsSuccess);
        var afterFirst = (await policies.ReadPolicyAsync()).Value;
        var second = first with
        {
            RequestId = Guid.Parse("00000000-0000-0000-0000-000000000013"),
            ExpectedPolicyRevision = 2,
            MaxRecords = initial.MaxRecords + 2
        };
        serializer.ShouldReject = entry => entry.EventName == "scheduling.fact-capacity-policy-changed.v1";
        await Assert.ThrowsAsync<InvalidOperationException>(() => policies.AdjustAsync(second, "test-operator", now, null));
        Assert.Equal(afterFirst, (await policies.ReadPolicyAsync()).Value);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policies.AdjustAsync(second, "test-operator", now, null, cancellation.Token));
        }
        Assert.Equal(afterFirst, (await policies.ReadPolicyAsync()).Value);
        serializer.ShouldReject = null;
        var retried = await policies.AdjustAsync(second, "test-operator", now.AddMinutes(1), null);
        Assert.True(retried.IsSuccess);
        Assert.Equal(3, retried.Value.PolicyRevision);
        Assert.Equal(2, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        await outbox.MarkDeliveredAsync(accepted.Value.EventId!.Value, now.AddMinutes(2));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(8)));
        Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.ControlCapacity.RetainedRecords);
        Assert.Equal(0, await cleanup.CleanupAsync(1, now.AddDays(8)));
        Assert.Equal(retried.Value, (await policies.AdjustAsync(second, "test-operator", now.AddDays(8), null)).Value);
        await outbox.MarkDeliveredAsync(retried.Value.EventId!.Value, now.AddMinutes(3));
        Assert.Equal(1, await cleanup.CleanupAsync(1, now.AddDays(8)));
        var final = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(0, final.ControlCapacity.RetainedRecords);
        Assert.Equal(0, final.ControlCapacity.RetainedPayloadBytes);
        Assert.Equal(3, final.PolicyRevision);
        Assert.Equal(initial.RetainedRecords, final.RetainedRecords);
        Assert.Equal(initial.RetainedPayloadBytes, final.RetainedPayloadBytes);
        Assert.Equal(planJson, JsonSerializer.Serialize(await plans.FindAsync(plan.Id)));
        Assert.Equal(decisionJson, JsonSerializer.Serialize(await plans.ReadDecisionsAsync(plan.Id.Value, 0, 10)));
        Assert.Equal(occurrenceJson, JsonSerializer.Serialize(await plans.ReadOccurrencesAsync(plan.Id.Value, 0, 10)));
        Assert.Equal(business, await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        var recovered = await plans.RetryOccurrenceAsync(occurrenceId, stopped.DeadLetteredAt.Value);
        Assert.True(recovered.IsSuccess);
        Assert.Equal("Pending", recovered.Value.DeliveryState);
        Assert.Equal(0, recovered.Value.AttemptCount);
        Assert.Equal(1, (await plans.ReadDecisionsAsync(plan.Id.Value, 0, 10)).Total);
        Assert.Equal(planJson, JsonSerializer.Serialize(await plans.FindAsync(plan.Id)));
    }

    private sealed class PersistentHistoryApp(string connection, RejectingEventSerializer serializer)
        : PersistentIdentityApp(connection, schedulingWorkerEnabled: false)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(serializer));
        }
    }
}
