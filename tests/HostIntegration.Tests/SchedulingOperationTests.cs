using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SchedulingOperationTests(JourneyDatabaseTemplates databases)
{
    [Fact]
    public async Task PlanWithoutOperationOrigin_RetainsItsKnownDelegator_WithoutInventingAParentOperation()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TaskRegistry>();
        var created = await registry.DefineAsync(TaskCode.Create("known-delegator").Value, TimeSpan.FromHours(1),
            ScheduleTarget.Create("costing.recalculate", Guid.NewGuid()).Value, "42");
        Assert.True(created.IsSuccess);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IScheduledTaskStore>().ReadExecutionOriginAsync(created.Value.Id));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync()).Triggered);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "schedule").ToArray();
        Assert.Equal(2, phases.Length);
        var finished = Assert.Single(phases, item => item.Phase == "finished");
        Assert.Null(finished.ActorId);
        Assert.Equal("42", finished.Metadata!.InitiatorId);
        Assert.Null(finished.Metadata.ParentOperationId);
        Assert.Null(finished.Metadata.ParentSource);
        Assert.Equal(finished.OperationId, finished.Metadata.RootOperationId);
        var occurrences = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(10, DateTimeOffset.UtcNow);
        using var payload = JsonDocument.Parse(Assert.Single(occurrences, item => item.EventName == ScheduleTriggeredV1.Name).Payload);
        var origin = payload.RootElement.GetProperty("executionOrigin");
        Assert.Equal("42", origin.GetProperty("initiatorId").GetString());
        Assert.Equal(finished.OperationId, origin.GetProperty("rootOperationId").GetGuid());
    }

    [PostgresFact]
    public async Task CancelDuringTriggerCommit_PreservesCanceledObservation_WithoutRegisteringAnOccurrence()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
            new { code = "cancel-trigger", intervalSeconds = 3600, targetKind = "costing.recalculate", targetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM scheduling.plans FOR UPDATE", blocker, transaction))
        {
            await command.ExecuteScalarAsync();
        }
        await using var scope = app.Services.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        var attempt = scope.ServiceProvider.GetRequiredService<ScheduleRunner>().RunOnceAsync(cancellation.Token);
        try
        {
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
                if ((long)(await waiting.ExecuteScalarAsync(timeout.Token))! > 0) { break; }
                await Task.Delay(20, timeout.Token);
            }
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
            try { await attempt; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        using var history = await client.GetAsync(new Uri($"/api/scheduling/tasks/{planId}/occurrences", UriKind.Relative));
        Assert.Empty((await history.Content.ReadApiDataAsync()).EnumerateArray());
        var registry = scope.ServiceProvider.GetRequiredService<TaskRegistry>();
        Assert.Equal(1, (await registry.FindAsync(new ScheduledTaskId(planId)))!.Version);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "schedule").ToArray();
        Assert.Equal(2, phases.Length);
        var started = Assert.Single(phases, item => item.Phase == "started");
        var finished = Assert.Single(phases, item => item.Phase == "finished");
        Assert.Equal(started.OperationId, finished.OperationId);
        Assert.Equal("canceled", finished.Outcome);
        Assert.Null(finished.ActorId);
        Assert.NotNull(finished.Metadata!.InitiatorId);
        Assert.Equal(planId, finished.Metadata.SchedulePlanId);
        Assert.Equal(1, finished.Metadata.ScheduleExpectedVersion);
        Assert.Equal(started.Metadata!.ScheduleDecisionId, finished.Metadata.ScheduleDecisionId);
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var onlyFact = Assert.Single(pending);
        Assert.Equal(PlanCommittedV1.Name, onlyFact.EventName);
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        Assert.Equal("created", serializer.Deserialize<PlanCommittedV1>(onlyFact.Payload).Operation);
    }

    [PostgresFact]
    public async Task PlanCreationOrigin_SurvivesRestart_AndFlowsIntoTheTriggeredOccurrence()
    {
        await using var database = await databases.CreateAsync();
        OperationObservedV1 creation;
        long planId;
        var targetId = Guid.NewGuid();
        await using (var app = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false))
        {
            using var client = app.CreateClient();
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
            using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative),
                new { code = "linked-plan", intervalSeconds = 3600, targetKind = "costing.recalculate", targetId });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            planId = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
            var correlation = Assert.Single(created.Headers.GetValues("X-Correlation-Id"));
            await using var scope = app.Services.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            creation = await OperationEndpointInventoryTests.WaitAsync(journal, correlation);
            Assert.NotNull(creation.ActorId);
        }

        await using var restarted = new PersistentIdentityApp(database.ConnectionString, "schedule-root-password", schedulingWorkerEnabled: false);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var runner = restartedScope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        Assert.Equal(1, (await runner.RunOnceAsync()).Triggered);
        var outbox = restartedScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(SchedulingInfrastructureServiceCollectionExtensions.OutboxKey);
        var message = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow), item => item.EventName == ScheduleTriggeredV1.Name);
        using var payload = JsonDocument.Parse(message.Payload);
        Assert.Equal(planId, payload.RootElement.GetProperty("planId").GetInt64());
        Assert.True(payload.RootElement.TryGetProperty("executionOrigin", out var origin), "计划触发须保留原始委托操作的关联。");
        Assert.Equal(creation.OperationId, origin.GetProperty("rootOperationId").GetGuid());
        Assert.Equal("platform", origin.GetProperty("rootSource").GetString());
        Assert.Equal(creation.ActorId, origin.GetProperty("initiatorId").GetString());
        Assert.Equal(creation.TraceId, origin.GetProperty("traceId").GetString());
        Assert.Equal(creation.Metadata!.CorrelationId, origin.GetProperty("correlationId").GetString());

        var triggerJournal = restartedScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observations = (await OperationEndpointInventoryTests.ReadAsync(triggerJournal)).Where(item => item.Kind == "schedule").ToArray();
        Assert.Equal(2, observations.Length);
        var started = Assert.Single(observations, item => item.Phase == "started");
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal(started.OperationId, finished.OperationId);
        Assert.Equal("accepted", finished.Outcome);
        Assert.Null(finished.ActorId);
        Assert.Equal(creation.ActorId, finished.Metadata!.InitiatorId);
        Assert.Equal(creation.OperationId, finished.Metadata.ParentOperationId);
        Assert.Equal(creation.OperationId, finished.Metadata.RootOperationId);
        Assert.Equal(finished.OperationId, origin.GetProperty("operationId").GetGuid());
        var triggerMetadata = JsonSerializer.SerializeToElement(finished.Metadata, JsonSerializerOptions.Web);
        Assert.Equal(planId, triggerMetadata.GetProperty("schedulePlanId").GetInt64());
        Assert.Equal(1, triggerMetadata.GetProperty("scheduleExpectedVersion").GetInt64());
        Assert.Equal(message.Id, triggerMetadata.GetProperty("scheduleDecisionId").GetGuid());

        var receiver = restartedScope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(OperationObservedV1.Name);
        var serializer = restartedScope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        Task<bool> DeliverAsync(OperationObservedV1 observation) => receiver.HandleAsync(new EventEnvelope
        {
            MessageId = observation.EventId,
            EventName = observation.EventName,
            OccurredAt = observation.OccurredAt,
            Payload = serializer.Serialize(observation),
        });
        Assert.True(await DeliverAsync(finished));
        Assert.True(await DeliverAsync(started));
        Assert.True(await DeliverAsync(finished));
        foreach (var changed in new[]
        {
            finished.Metadata with { SchedulePlanId = planId + 1 },
            finished.Metadata with { ScheduleExpectedVersion = 2 },
            finished.Metadata with { ScheduleDecisionId = Guid.NewGuid() },
        })
        {
            Assert.False(await DeliverAsync(finished with { Metadata = changed }));
        }
        using (var client = restarted.CreateClient())
        {
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "schedule-root-password");
            using var investigated = await client.GetAsync(new Uri($"/api/auditing/operations?operationId={finished.OperationId}&outcome=accepted", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, investigated.StatusCode);
            var found = Assert.Single((await investigated.Content.ReadApiDataAsync()).EnumerateArray());
            Assert.Equal("schedule", found.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, found.GetProperty("actorId").ValueKind);
            var metadata = found.GetProperty("metadata");
            Assert.Equal(planId, metadata.GetProperty("schedulePlanId").ReadHttpInt64());
            Assert.Equal(1, metadata.GetProperty("scheduleExpectedVersion").ReadHttpInt64());
            Assert.Equal(message.Id, metadata.GetProperty("scheduleDecisionId").GetGuid());
            Assert.Equal(creation.ActorId, metadata.GetProperty("initiatorId").GetString());
            Assert.Equal(creation.OperationId, metadata.GetProperty("rootOperationId").GetGuid());
        }

        var registry = restartedScope.ServiceProvider.GetRequiredService<TaskRegistry>();
        var taskId = new ScheduledTaskId(planId);
        var plan = (await registry.FindAsync(taskId))!;
        Assert.True((await registry.UpdateRuleAsync(taskId, plan.Version, new ScheduleRuleInput("Interval", IntervalSeconds: 1800))).IsSuccess);
        plan = (await registry.FindAsync(taskId))!;
        Assert.True((await registry.PauseAsync(taskId, plan.Version)).IsSuccess);
        plan = (await registry.FindAsync(taskId))!;
        Assert.True((await registry.ResumeAsync(taskId, plan.Version)).IsSuccess);
        var preserved = await restartedScope.ServiceProvider.GetRequiredService<IScheduledTaskStore>().ReadExecutionOriginAsync(taskId);
        Assert.NotNull(preserved);
        Assert.Equal(creation.OperationId, preserved.RootOperationId);
        Assert.Equal(creation.ActorId, preserved.InitiatorId);

        await using var costingDatabase = await databases.CreateAsync("costing");
        var costingServices = new ServiceCollection();
        costingServices.AddLogging();
        costingServices.AddNexusStackApplication();
        costingServices.AddCostingPostgres(costingDatabase.ConnectionString);
        await using var costing = costingServices.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var costingScope = costing.CreateAsyncScope();
        var sender = costingScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), targetId, 0, 80m, 20m))).IsSuccess);
        Assert.True(await costingScope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(ScheduleTriggeredV1.Name)
            .HandleAsync(message.ToEnvelope()));
        var accepted = (await sender.QueryAsync(new GetCostCalculation(message.Id))).Value;
        Assert.NotNull(accepted.ExecutionOrigin);
        Assert.Equal(creation.OperationId, accepted.ExecutionOrigin.RootOperationId);
        Assert.Equal(creation.ActorId, accepted.ExecutionOrigin.InitiatorId);
        Assert.Equal("platform", accepted.ExecutionOrigin.RootSource);
    }
}
