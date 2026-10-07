using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Endpoints;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class TaskOperationTests(JourneyDatabaseTemplates databases)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresFact]
    public async Task ExpiredPricingAttempt_IsObservedAsLeaseLost_WithoutChangingItsReplacementResult()
    {
        await using var database = await databases.CreateAsync("pricing");
        await using var original = CreatePricingApp(database.ConnectionString, "original-initiator",
            new PricingTaskOptions { LeaseDuration = TimeSpan.FromMilliseconds(500) });
        await using var scope = original.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        var accepted = await sender.SendAsync(request);
        Assert.True(accepted.IsSuccess);
        var old = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        await using var replacement = CreatePricingApp(database.ConnectionString, null);
        await using var replacementScope = replacement.Services.CreateAsyncScope();
        var replacementSender = replacementScope.ServiceProvider.GetRequiredService<ISender>();
        var current = (await replacementSender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(2, current.Epoch);
        Assert.False((await sender.SendAsync(new CompletePricingWork(old.TaskId, old.Epoch))).Value);
        Assert.True((await replacementSender.SendAsync(new CompletePricingWork(current.TaskId, current.Epoch))).Value);
        var oldJournal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var oldAttempt = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(oldJournal), item => item.Kind == "task" && item.Phase == "finished");
        var newJournal = replacementScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var newAttempt = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(newJournal), item => item.Kind == "task" && item.Phase == "finished");
        Assert.Equal("lease_lost", oldAttempt.Outcome);
        Assert.Equal("completed", newAttempt.Outcome);
        Assert.NotEqual(oldAttempt.OperationId, newAttempt.OperationId);
        Assert.Equal(1, oldAttempt.Metadata!.TaskEpoch);
        Assert.Equal(2, newAttempt.Metadata!.TaskEpoch);
        foreach (var attempt in new[] { oldAttempt, newAttempt })
        {
            Assert.Null(attempt.ActorId);
            Assert.Equal(request.RequestId, attempt.Metadata!.TaskId);
            Assert.Equal("original-initiator", attempt.Metadata.InitiatorId);
            Assert.Equal(accepted.Value.ExecutionOrigin!.OperationId, attempt.Metadata.RootOperationId);
        }
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(request.ItemId))).Value.BreakEvenPrice);
        Assert.Equal(new[] { "Expired", "Succeeded" }, (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.History.Select(item => item.Outcome));
    }

    [PostgresFact]
    public async Task ReplacedCostInputs_AreObservedAsSuperseded_AndOnlyCurrentCalculationPublishesAResult()
    {
        await using var database = await databases.CreateAsync("costing");
        await using var app = CreateCostingApp(database.ConnectionString, "original-initiator");
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var first = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Assert.True((await sender.SendAsync(first)).IsSuccess);
        var old = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        var latest = first with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 100m };
        Assert.True((await sender.SendAsync(latest)).IsSuccess);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(old.TaskId, old.Epoch))).Value);
        Assert.Equal("Superseded", (await sender.QueryAsync(new GetCostCalculation(first.RequestId))).Value.State);
        Assert.Null((await sender.QueryAsync(new GetCostSheet(first.ItemId))).Value.UnitCost);
        var businessOutbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        Assert.DoesNotContain(await businessOutbox.ReadPendingAsync(10, DateTimeOffset.UtcNow), entry => entry.EventName == CostCalculatedV1.Name);
        var current = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(latest.RequestId, current.TaskId);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(current.TaskId, current.Epoch))).Value);
        Assert.Equal(120m, (await sender.QueryAsync(new GetCostSheet(first.ItemId))).Value.UnitCost);
        Assert.Equal(latest.RequestId, Assert.Single(await businessOutbox.ReadPendingAsync(10, DateTimeOffset.UtcNow), item => item.EventName == CostCalculatedV1.Name).Id);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "task" && item.Phase == "finished").ToArray();
        Assert.Equal(2, attempts.Length);
        var superseded = Assert.Single(attempts, item => item.Metadata!.TaskId == first.RequestId);
        var completed = Assert.Single(attempts, item => item.Metadata!.TaskId == latest.RequestId);
        Assert.Equal("superseded", superseded.Outcome);
        Assert.Equal("completed", completed.Outcome);
        Assert.NotEqual(superseded.OperationId, completed.OperationId);
        Assert.Null(superseded.ActorId);
        Assert.Null(completed.ActorId);
    }

    [PostgresFact]
    public async Task CanceledInputRead_RecordsCanceledExecution_WithoutCancelingTheBusinessTask()
    {
        await using var database = await databases.CreateAsync("pricing");
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        await using (var producer = CreatePricingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = producer.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command)).IsSuccess);
        }
        await using var worker = CreatePricingApp(database.ConnectionString, null);
        await using var workerScope = worker.Services.CreateAsyncScope();
        var sender = workerScope.ServiceProvider.GetRequiredService<ISender>();
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch), cancellation.Token));
        var journal = workerScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observations = (await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow))
            .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload)).ToArray();
        Assert.Equal(2, observations.Length);
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal("canceled", finished.Outcome);
        Assert.Equal(command.RequestId, finished.Metadata!.TaskId);
        Assert.Equal(lease.Epoch, finished.Metadata.TaskEpoch);
        Assert.Null(finished.ActorId);
        Assert.Null(finished.Metadata.InitiatorId);
        Assert.Null(finished.Metadata.ParentOperationId);
        Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(command.RequestId))).Value.State);
        Assert.Null((await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.BreakEvenPrice);
    }

    [PostgresFact]
    public async Task RolledBackPricingAttempt_StaysFailed_AndManualRetryKeepsTheOriginalInitiator()
    {
        await using var database = await databases.CreateAsync("pricing");
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Guid rootOperation;
        await using (var producer = CreatePricingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = producer.Services.CreateAsyncScope())
        {
            rootOperation = (await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command)).Value.ExecutionOrigin!.OperationId;
        }
        await using var faultConnection = new NpgsqlConnection(database.ConnectionString);
        await faultConnection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION pricing.operation_probe_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State" = 'Succeeded' THEN RAISE EXCEPTION 'private-write-failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER operation_probe_failure BEFORE UPDATE ON pricing.tasks
            FOR EACH ROW EXECUTE FUNCTION pricing.operation_probe_failure();
            """, faultConnection))
        {
            await inject.ExecuteNonQueryAsync();
        }
        Guid failedOperation;
        long failedEpoch;
        await using (var worker = CreatePricingApp(database.ConnectionString, null, new PricingTaskOptions { MaxAttempts = 1 }))
        {
            await using (var scope = worker.Services.CreateAsyncScope())
            {
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
                failedEpoch = lease.Epoch;
                await Assert.ThrowsAsync<DbUpdateException>(() => sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch)));
                Assert.Null((await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.BreakEvenPrice);
                Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(command.RequestId))).Value.State);
            }
            await using var failureScope = worker.Services.CreateAsyncScope();
            Assert.True((await failureScope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new FailPricingWork(command.RequestId, failedEpoch))).Value);
            var journal = failureScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var messages = await journal.ReadPendingAsync(20, DateTimeOffset.UtcNow);
            Assert.Equal(2, messages.Count);
            Assert.All(messages, message => Assert.DoesNotContain("private-write-failure", message.Payload, StringComparison.Ordinal));
            var observations = messages.Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload));
            var failed = Assert.Single(observations, item => item.Phase == "finished");
            failedOperation = failed.OperationId;
            Assert.Equal("failed", failed.Outcome);
            Assert.Equal(failedEpoch, failed.Metadata!.TaskEpoch);
            Assert.Equal(rootOperation, failed.Metadata.RootOperationId);
        }
        await using (var restore = new NpgsqlCommand("DROP TRIGGER operation_probe_failure ON pricing.tasks; DROP FUNCTION pricing.operation_probe_failure();", faultConnection))
        {
            await restore.ExecuteNonQueryAsync();
        }
        await using (var operatorApp = CreatePricingApp(database.ConnectionString, "retry-operator"))
        await using (var scope = operatorApp.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new RetryPricingWork(command.RequestId, failedEpoch))).IsSuccess);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var retried = Assert.Single((await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow))
                .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload)),
                item => item.Phase == "finished");
            Assert.Equal("accepted", retried.Outcome);
            Assert.Equal("retry-operator", retried.ActorId);
            Assert.Equal(command.RequestId, retried.Metadata!.TaskId);
            Assert.Null(retried.Metadata.TaskEpoch);
        }
        await using var restarted = CreatePricingApp(database.ConnectionString, null);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedSender = restartedScope.ServiceProvider.GetRequiredService<ISender>();
        var newLease = (await restartedSender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(failedEpoch + 1, newLease.Epoch);
        Assert.True((await restartedSender.SendAsync(new CompletePricingWork(newLease.TaskId, newLease.Epoch))).Value);
        Assert.Equal(100m, (await restartedSender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.BreakEvenPrice);
        var resultJournal = restartedScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var finished = Assert.Single((await resultJournal.ReadPendingAsync(10, DateTimeOffset.UtcNow))
            .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload)), item => item.Phase == "finished");
        Assert.NotEqual(failedOperation, finished.OperationId);
        Assert.Equal("completed", finished.Outcome);
        Assert.Null(finished.ActorId);
        Assert.Equal("original-initiator", finished.Metadata!.InitiatorId);
        Assert.Equal(rootOperation, finished.Metadata.RootOperationId);
        Assert.Equal(newLease.Epoch, finished.Metadata.TaskEpoch);
    }

    [PostgresFact]
    public async Task CostMessage_RejectsMalformedOrigin_BeforeInboxOrTaskAcceptance()
    {
        await using var database = await databases.CreateAsync("pricing");
        var operationId = Guid.NewGuid();
        var origin = new ExecutionOrigin(operationId, "costing", operationId, "costing", "original-initiator", operationId.ToString("N"));
        var message = new CostCalculatedV1
        {
            EventId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            CostRevision = 1,
            UnitCost = 100m,
            OccurredAt = DateTimeOffset.UtcNow,
            ExecutionOrigin = origin,
        };
        await using var app = CreatePricingApp(database.ConnectionString, null);
        await using var scope = app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        ExecutionOrigin[] malformed =
        [
            origin with { OperationId = Guid.Empty },
            origin with { Source = new string('s', 65) },
            origin with { RootOperationId = Guid.Empty },
            origin with { RootSource = null! },
            origin with { InitiatorId = "unsafe\nactor" },
            origin with { InitiatorId = new string('a', 201) },
            origin with { TraceId = "" },
            origin with { CorrelationId = "query=value" },
        ];
        foreach (var invalid in malformed)
        {
            Assert.False(await processor.HandleAsync(OutboxEntry.From(message with { ExecutionOrigin = invalid },
                new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
        }
        Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(message.EventId))).IsFailure);
        Assert.True(await processor.HandleAsync(OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
        var accepted = (await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(message.EventId))).Value.ExecutionOrigin!;
        Assert.NotEqual(origin.OperationId, accepted.OperationId);
        Assert.Equal("pricing", accepted.Source);
        Assert.Equal(origin.RootOperationId, accepted.RootOperationId);
        Assert.Equal(origin.InitiatorId, accepted.InitiatorId);
        var observation = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)),
            item => item.Kind == "message" && item.Outcome == "accepted");
        Assert.Equal(accepted.OperationId, observation.OperationId);
        Assert.Equal(origin.OperationId, observation.Metadata!.ParentOperationId);
    }

    [PostgresFact]
    public async Task CostMessageReplay_CannotChangeOrRemoveItsOriginalExecutionAssociation()
    {
        await using var database = await databases.CreateAsync("pricing");
        var operationId = Guid.NewGuid();
        var origin = new ExecutionOrigin(operationId, "costing", operationId, "costing", "original-initiator", operationId.ToString("N"));
        var message = new CostCalculatedV1
        {
            EventId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            CostRevision = 1,
            UnitCost = 100m,
            OccurredAt = DateTimeOffset.UtcNow,
            ExecutionOrigin = origin,
        };
        ExecutionOrigin acceptedOrigin;
        await using (var app = CreatePricingApp(database.ConnectionString, null))
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name)
                .HandleAsync(OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
            acceptedOrigin = (await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(message.EventId))).Value.ExecutionOrigin!;
            Assert.Equal(origin.RootOperationId, acceptedOrigin.RootOperationId);
            Assert.Equal(origin.InitiatorId, acceptedOrigin.InitiatorId);
            Assert.Equal("pricing", acceptedOrigin.Source);
        }
        await using var restarted = CreatePricingApp(database.ConnectionString, null);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var processor = restartedScope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        Assert.True(await processor.HandleAsync(OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
        Assert.False(await processor.HandleAsync(OutboxEntry.From(message with
        {
            ExecutionOrigin = origin with { InitiatorId = "replacement" },
        }, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
        Assert.False(await processor.HandleAsync(OutboxEntry.From(message with { ExecutionOrigin = null },
            new SystemTextJsonIntegrationEventSerializer()).ToEnvelope()));
        var task = (await restartedScope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetRecalculation(message.EventId))).Value;
        Assert.Equal(acceptedOrigin, task.ExecutionOrigin);
    }

    [PostgresFact]
    public async Task CostResultMessage_PreservesRootInitiatorAndImmediateAttempt_InPricingAfterRestart()
    {
        await using var costDatabase = await databases.CreateAsync("costing");
        await using var priceDatabase = await databases.CreateAsync("pricing");
        var command = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Guid rootOperation;
        Guid costAttempt;
        Guid costAcceptance;
        EventEnvelope envelope;
        await using (var costing = CreateCostingApp(costDatabase.ConnectionString, "original-initiator"))
        await using (var scope = costing.Services.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            rootOperation = (await sender.SendAsync(command)).Value.ExecutionOrigin!.OperationId;
            var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var observations = (await journal.ReadPendingAsync(20, DateTimeOffset.UtcNow))
                .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload));
            costAttempt = Assert.Single(observations, item => item.Kind == "task" && item.Phase == "finished").OperationId;
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            envelope = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.UtcNow),
                message => message.EventName == CostCalculatedV1.Name).ToEnvelope();
        }

        await using (var pricing = CreatePricingApp(priceDatabase.ConnectionString, null))
        await using (var scope = pricing.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name).HandleAsync(envelope));
            var observation = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)),
                item => item.Kind == "message" && item.Outcome == "accepted");
            costAcceptance = observation.OperationId;
            Assert.Equal(costAttempt, observation.Metadata!.ParentOperationId);
            Assert.Equal("costing", observation.Metadata.ParentSource);
        }
        await using var restarted = CreatePricingApp(priceDatabase.ConnectionString, null);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedSender = restartedScope.ServiceProvider.GetRequiredService<ISender>();
        var task = (await restartedSender.QueryAsync(new GetRecalculation(command.RequestId))).Value;
        Assert.NotNull(task.ExecutionOrigin);
        Assert.Equal(rootOperation, task.ExecutionOrigin.RootOperationId);
        Assert.Equal(costAcceptance, task.ExecutionOrigin.OperationId);
        Assert.Equal("pricing", task.ExecutionOrigin.Source);
        Assert.Equal("costing", task.ExecutionOrigin.RootSource);
        Assert.Equal("original-initiator", task.ExecutionOrigin.InitiatorId);
        Assert.True(await restartedScope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name).HandleAsync(envelope));
        var pricingLease = (await restartedSender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await restartedSender.SendAsync(new CompletePricingWork(pricingLease.TaskId, pricingLease.Epoch))).Value);
        var pricingJournal = restartedScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var pricingObservations = (await pricingJournal.ReadPendingAsync(20, DateTimeOffset.UtcNow))
            .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload));
        var finished = Assert.Single(pricingObservations, item => item.Kind == "task" && item.Phase == "finished");
        Assert.Null(finished.ActorId);
        Assert.Equal("completed", finished.Outcome);
        Assert.Equal(costAcceptance, finished.Metadata!.ParentOperationId);
        Assert.Equal(rootOperation, finished.Metadata.RootOperationId);
        Assert.Equal("pricing", finished.Metadata.ParentSource);
        Assert.Equal("original-initiator", finished.Metadata.InitiatorId);
    }

    [PostgresFact]
    public async Task PricingAttempt_RecordsSystemExecution_LinkedToTheAcceptedTaskAndInitiator()
    {
        await using var database = await databases.CreateAsync("pricing");
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        Guid originId;
        await using (var producer = CreatePricingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = producer.Services.CreateAsyncScope())
        {
            var accepted = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
            Assert.NotNull(accepted.Value.ExecutionOrigin);
            originId = accepted.Value.ExecutionOrigin.OperationId;
        }

        await using var worker = CreatePricingApp(database.ConnectionString, null);
        await using var workerScope = worker.Services.CreateAsyncScope();
        var sender = workerScope.ServiceProvider.GetRequiredService<ISender>();
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value;
        Assert.NotNull(lease);
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.BreakEvenPrice);
        var journal = workerScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observations = (await journal.ReadPendingAsync(20, DateTimeOffset.UtcNow))
            .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload))
            .ToArray();
        Assert.Equal(2, observations.Length);
        Assert.All(observations, item => Assert.Equal("task", item.Kind));
        var started = Assert.Single(observations, item => item.Phase == "started");
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal(started.OperationId, finished.OperationId);
        Assert.Equal("completed", finished.Outcome);
        Assert.Null(finished.ActorId);
        Assert.Null(finished.StatusCode);
        Assert.NotNull(finished.Metadata);
        Assert.Equal("pricing.calculate", finished.Metadata.Action);
        Assert.Equal(command.RequestId, finished.Metadata.TaskId);
        Assert.Equal(lease.Epoch, finished.Metadata.TaskEpoch);
        Assert.Equal(originId, finished.Metadata.ParentOperationId);
        Assert.Equal(originId, finished.Metadata.RootOperationId);
        Assert.Equal("original-initiator", finished.Metadata.InitiatorId);
    }

    [PostgresFact]
    public async Task CostingAttempt_RecordsSystemExecution_LinkedToTheAcceptedTaskAndInitiator()
    {
        await using var database = await databases.CreateAsync("costing");
        var command = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Guid originId;
        await using (var producer = CreateCostingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = producer.Services.CreateAsyncScope())
        {
            var accepted = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(command);
            Assert.NotNull(accepted.Value.ExecutionOrigin);
            originId = accepted.Value.ExecutionOrigin.OperationId;
        }

        await using var worker = CreateCostingApp(database.ConnectionString, null);
        await using var workerScope = worker.Services.CreateAsyncScope();
        var sender = workerScope.ServiceProvider.GetRequiredService<ISender>();
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value;
        Assert.NotNull(lease);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetCostCalculation(command.RequestId))).Value.State);
        var journal = workerScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var observations = (await journal.ReadPendingAsync(20, DateTimeOffset.UtcNow))
            .Select(message => new SystemTextJsonIntegrationEventSerializer().Deserialize<OperationObservedV1>(message.Payload))
            .ToArray();
        Assert.Equal(2, observations.Length);
        Assert.All(observations, item => Assert.Equal("task", item.Kind));
        var started = Assert.Single(observations, item => item.Phase == "started");
        var finished = Assert.Single(observations, item => item.Phase == "finished");
        Assert.Equal(started.OperationId, finished.OperationId);
        Assert.Equal("completed", finished.Outcome);
        Assert.Null(finished.ActorId);
        Assert.Null(finished.StatusCode);
        var metadata = JsonSerializer.SerializeToElement(finished.Metadata, JsonOptions);
        Assert.Equal("costing.calculate", metadata.GetProperty("action").GetString());
        Assert.Equal(command.RequestId, metadata.GetProperty("taskId").GetGuid());
        Assert.Equal(lease.Epoch, metadata.GetProperty("taskEpoch").GetInt64());
        Assert.Equal(originId, metadata.GetProperty("parentOperationId").GetGuid());
        Assert.Equal(originId, metadata.GetProperty("rootOperationId").GetGuid());
        Assert.Equal("original-initiator", metadata.GetProperty("initiatorId").GetString());
    }

    [PostgresFact]
    public Task AcceptedPricingTask_PreservesItsOperationAndInitiator_AcrossRestartAndReplay() =>
        CheckPricingOriginAsync(feeOnly: false);

    [PostgresFact]
    public Task AcceptedPricingTaskForFee_PreservesItsOperationAndInitiator_AcrossRestartAndReplay() =>
        CheckPricingOriginAsync(feeOnly: true);

    private async Task CheckPricingOriginAsync(bool feeOnly)
    {
        await using var database = await databases.CreateAsync("pricing");
        var initial = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        ICommand<RecalculationStatus> command = initial;
        Guid operationId;
        Guid taskId;
        await using (var app = CreatePricingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            if (feeOnly)
            {
                Assert.True((await sender.SendAsync(initial)).IsSuccess);
                var quote = (await sender.QueryAsync(new GetPriceQuote(initial.ItemId))).Value;
                command = new UpdatePricingFee(Guid.NewGuid(), initial.ItemId, quote.Version, 0.25m);
            }
            var accepted = await sender.SendAsync(command);
            Assert.True(accepted.IsSuccess);
            taskId = accepted.Value.TaskId;
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
            var finished = Assert.Single(messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
                .Deserialize<OperationObservedV1>(message.Payload)), item => item.Phase == "finished"
                    && item.Metadata?.Action == "command." + command.GetType().Name);
            operationId = finished.OperationId;
            Assert.Equal("accepted", finished.Outcome);
            Assert.Equal(taskId, finished.Metadata!.TaskId);
            Assert.Null(finished.Metadata.TaskEpoch);
            AssertOrigin(accepted.Value, operationId, "pricing");
        }

        await using var restarted = CreatePricingApp(database.ConnectionString, "different-replay-caller");
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedSender = restartedScope.ServiceProvider.GetRequiredService<ISender>();
        AssertOrigin((await restartedSender.QueryAsync(new GetRecalculation(taskId))).Value, operationId, "pricing");
        AssertOrigin((await restartedSender.SendAsync(command)).Value, operationId, "pricing");
    }

    [PostgresFact]
    public async Task AcceptedCostTask_PreservesItsOperationAndInitiator_AcrossRestartAndReplay()
    {
        await using var database = await databases.CreateAsync("costing");
        var command = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Guid operationId;
        await using (var app = CreateCostingApp(database.ConnectionString, "original-initiator"))
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var accepted = await sender.SendAsync(command);
            Assert.True(accepted.IsSuccess);
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var messages = await journal.ReadPendingAsync(10, DateTimeOffset.UtcNow);
            var finished = Assert.Single(messages.Select(message => new SystemTextJsonIntegrationEventSerializer()
                .Deserialize<OperationObservedV1>(message.Payload)), item => item.Phase == "finished");
            operationId = finished.OperationId;
            Assert.Equal("accepted", finished.Outcome);
            Assert.Equal(command.RequestId, finished.Metadata!.TaskId);
            Assert.Null(finished.Metadata.TaskEpoch);
            AssertOrigin(accepted.Value, operationId);
        }

        await using var restarted = CreateCostingApp(database.ConnectionString, "different-replay-caller");
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedSender = restartedScope.ServiceProvider.GetRequiredService<ISender>();
        AssertOrigin((await restartedSender.QueryAsync(new GetCostCalculation(command.RequestId))).Value, operationId);
        AssertOrigin((await restartedSender.SendAsync(command)).Value, operationId);
    }

    private static void AssertOrigin(object status, Guid operationId, string source = "costing")
    {
        var json = JsonSerializer.SerializeToElement(status, JsonOptions);
        Assert.True(json.TryGetProperty("executionOrigin", out var origin), "任务查询应公开已持久化的执行来源。");
        Assert.NotEqual(JsonValueKind.Null, origin.ValueKind);
        Assert.Equal(operationId, origin.GetProperty("operationId").GetGuid());
        Assert.Equal(operationId, origin.GetProperty("rootOperationId").GetGuid());
        Assert.Equal(source, origin.GetProperty("source").GetString());
        Assert.Equal(source, origin.GetProperty("rootSource").GetString());
        Assert.Equal("original-initiator", origin.GetProperty("initiatorId").GetString());
    }

    internal static WebApplication CreateCostingApp(string connection, string? actor)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "costing");
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        builder.Services.AddCostingPostgres(connection);
        return builder.Build();
    }

    internal static WebApplication CreatePricingApp(string connection, string? actor, PricingTaskOptions? options = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationJournal:Storage:Provider"] = "Memory",
        });
        builder.Services.AddNexusStackApplication();
        builder.Services.AddOperationJournalModule(builder.Configuration, builder.Environment, "pricing");
        builder.Services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        builder.Services.AddPricingPostgres(connection, options);
        return builder.Build();
    }
}
