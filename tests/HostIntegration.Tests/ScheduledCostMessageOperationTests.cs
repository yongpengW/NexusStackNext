using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Contracts;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class ScheduledCostMessageOperationTests
{
    [PostgresFact]
    public Task FailedAcceptanceCommit_RollsBackReceiptTaskAndInbox_WhileKeepingTheFailedObservation() => InterruptedCommitAsync(cancel: false, targetExists: true);

    [PostgresFact]
    public Task CanceledAcceptanceCommit_RollsBackReceiptTaskAndInbox_AndRedeliveryCanRecover() => InterruptedCommitAsync(cancel: true, targetExists: true);

    [PostgresFact]
    public Task FailedRejectionCommit_IsNotAcknowledged_AndOnlyCommittedRejectionIsStable() => InterruptedCommitAsync(cancel: false, targetExists: false);

    private static async Task InterruptedCommitAsync(bool cancel, bool targetExists)
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await using var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, "ambient-user");
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var sender = services.GetRequiredService<ISender>();
        var message = Trigger(Guid.NewGuid());
        if (targetExists) { Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), message.TargetId, 0, 80m, 20m))).IsSuccess); }
        await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        await barrier.OpenAsync();
        var effect = cancel ? "PERFORM pg_advisory_xact_lock(640067);" : "RAISE EXCEPTION 'Injected schedule receipt failure';";
        await using (var install = new NpgsqlCommand($"""
            SELECT pg_advisory_lock(640067);
            CREATE FUNCTION costing.interrupt_schedule_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN {effect} RETURN NEW; END $$;
            CREATE TRIGGER interrupt_schedule_receipt BEFORE INSERT ON costing.schedule_receipts
            FOR EACH ROW EXECUTE FUNCTION costing.interrupt_schedule_receipt();
            """, barrier)) { await install.ExecuteNonQueryAsync(); }
        using var cancellation = new CancellationTokenSource();
        var consumer = Processor(services);
        var pending = consumer.HandleAsync(Envelope(message), cancellation.Token);
        try
        {
            if (cancel)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var blocked = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)))", barrier);
                blocked.Parameters.AddWithValue("barrier", barrier.ProcessID);
                while (!Equals(true, await blocked.ExecuteScalarAsync(budget.Token))) { await Task.Delay(20, budget.Token); }
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else { await Assert.ThrowsAsync<DbUpdateException>(() => pending); }
        }
        finally
        {
            await cancellation.CancelAsync();
            await using (var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(640067)", barrier)) { await unlock.ExecuteNonQueryAsync(); }
            try { await pending; }
            catch (Exception error) when (error is OperationCanceledException or DbUpdateException) { }
            await using var restore = new NpgsqlCommand("DROP TRIGGER interrupt_schedule_receipt ON costing.schedule_receipts; DROP FUNCTION costing.interrupt_schedule_receipt()", barrier);
            await restore.ExecuteNonQueryAsync();
        }
        Assert.True((await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).IsFailure);
        Assert.True((await sender.QueryAsync(new GetCostCalculation(message.EventId))).IsFailure);
        var journal = services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var failed = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Kind == "message" && item.Phase == "finished");
        Assert.Equal(cancel ? "canceled" : "failed", failed.Outcome);
        Assert.Null(failed.ActorId);
        Assert.Null(services.GetRequiredService<IExecutionContext>().Capture());
        Assert.True(await consumer.HandleAsync(Envelope(message)));
        var outcome = targetExists ? "accepted" : "rejected";
        var finished = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "message" && item.Phase == "finished").ToArray();
        Assert.Equal(2, finished.Length);
        Assert.Contains(finished, item => item.OperationId == failed.OperationId && item.Outcome == failed.Outcome);
        var recovered = Assert.Single(finished, item => item.Outcome == outcome);
        Assert.NotEqual(failed.OperationId, recovered.OperationId);
        var receipt = (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value;
        Assert.Equal(targetExists ? "Accepted" : "Rejected", receipt.Decision);
        var task = await sender.QueryAsync(new GetCostCalculation(message.EventId));
        if (targetExists) { Assert.Equal(recovered.OperationId, task.Value.ExecutionOrigin!.OperationId); }
        else { Assert.True(task.IsFailure); }
        Assert.Equal(targetExists ? 1 : 0, (await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow)).Count);
    }

    [PostgresFact]
    public async Task ConcurrentDelivery_HasOneAcceptance_AndRestartedWorkKeepsItsOriginalMessageOperation()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        var parent = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "42", "schedule-trace", "schedule-correlation");
        var message = Trigger(Guid.NewGuid()) with { ExecutionOrigin = parent };
        Guid acceptance;
        await using (var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, "ambient-user"))
        {
            await using (var setup = app.Services.CreateAsyncScope())
            {
                var sender = setup.ServiceProvider.GetRequiredService<ISender>();
                Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), message.TargetId, 0, 80m, 20m))).IsSuccess);
                var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
                Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            }
            var deliveries = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                await using var delivery = app.Services.CreateAsyncScope();
                return await Processor(delivery.ServiceProvider).HandleAsync(Envelope(message));
            }));
            Assert.All(deliveries, Assert.True);
            await using var check = app.Services.CreateAsyncScope();
            var journal = check.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "message" && item.Phase == "finished").ToArray();
            Assert.Equal(3, attempts.Length);
            Assert.Equal(2, attempts.Count(item => item.Outcome == "duplicate"));
            var accepted = Assert.Single(attempts, item => item.Outcome == "accepted");
            acceptance = accepted.OperationId;
            Assert.Equal(3, attempts.Select(item => item.OperationId).Distinct().Count());
            Assert.All(attempts, item =>
            {
                Assert.Null(item.ActorId);
                Assert.Equal("costing", item.Source);
                Assert.Equal("costing.schedule.accept", item.Metadata!.Action);
                Assert.Equal(ScheduleTriggeredV1.Name, item.Metadata.SubjectType);
                Assert.Equal(message.EventId.ToString("D"), item.Metadata.SubjectId);
                Assert.Equal(parent.OperationId, item.Metadata.ParentOperationId);
                Assert.Equal(parent.RootOperationId, item.Metadata.RootOperationId);
                Assert.Equal("42", item.Metadata.InitiatorId);
                Assert.Null(item.Metadata.TaskId);
                Assert.Null(item.Metadata.SchedulePlanId);
            });
            Assert.Null(check.ServiceProvider.GetRequiredService<IExecutionContext>().Capture());
            Assert.Equal(acceptance, (await check.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetCostCalculation(message.EventId))).Value.ExecutionOrigin!.OperationId);
        }
        await using var restarted = TaskOperationTests.CreateCostingApp(database.ConnectionString, "different-ambient-user");
        await using var scope = restarted.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        Assert.True(await Processor(services).HandleAsync(Envelope(message)));
        Assert.False(await Processor(services).HandleAsync(Envelope(message with { ExecutionOrigin = null })));
        Assert.False(await Processor(services).HandleAsync(Envelope(message with { ExecutionOrigin = parent with { InitiatorId = "replacement" } })));
        var query = services.GetRequiredService<ISender>();
        var receipt = (await query.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value;
        Assert.Equal("42", receipt.CreatedBy);
        var task = (await query.QueryAsync(new GetCostCalculation(message.EventId))).Value;
        Assert.Equal(acceptance, task.ExecutionOrigin!.OperationId);
        Assert.Equal("costing", task.ExecutionOrigin.Source);
        Assert.Equal(parent.RootOperationId, task.ExecutionOrigin.RootOperationId);
        var work = (await query.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(message.EventId, work.TaskId);
        Assert.True((await query.SendAsync(new CompleteCostingWork(work.TaskId, work.Epoch))).Value);
        var completed = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey)),
            item => item.Kind == "task" && item.Phase == "finished");
        Assert.Null(completed.ActorId);
        Assert.Equal(acceptance, completed.Metadata!.ParentOperationId);
        Assert.Equal("costing", completed.Metadata.ParentSource);
        Assert.Equal(parent.RootOperationId, completed.Metadata.RootOperationId);
        var results = (await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow))
            .Where(item => item.EventName == CostCalculatedV1.Name)
            .Select(item => new SystemTextJsonIntegrationEventSerializer().Deserialize<CostCalculatedV1>(item.Payload)).ToArray();
        Assert.Contains(results, item => item.ExecutionOrigin?.OperationId == completed.OperationId && item.ExecutionOrigin.InitiatorId == "42");
    }

    [PostgresFact]
    public async Task StableBusinessRejection_IsAcknowledgedButObservedAsRejected_AndRedeliveryCannotReinterpretIt()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await CostingDatabase.MigrateAsync(database.ConnectionString);
        await using var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, "ambient-user");
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var consumer = Processor(services);
        var message = Trigger(Guid.NewGuid());
        var sender = services.GetRequiredService<ISender>();
        Assert.True(await consumer.HandleAsync(Envelope(message)));
        var rejected = (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value;
        Assert.Equal("Rejected", rejected.Decision);
        var journal = services.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var first = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Phase == "finished");
        Assert.Equal("rejected", first.Outcome);
        Assert.Null(first.ActorId);
        Assert.Null(first.Metadata!.InitiatorId);
        Assert.Null(first.Metadata.ParentOperationId);
        Assert.Equal(first.OperationId, first.Metadata.RootOperationId);
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), message.TargetId, 0, 80m, 20m))).IsSuccess);
        Assert.True(await consumer.HandleAsync(Envelope(message)));
        Assert.Equal(rejected, (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value);
        Assert.True((await sender.QueryAsync(new GetCostCalculation(message.EventId))).IsFailure);
        Assert.False(await consumer.HandleAsync(Envelope(message with { CreatedBy = "changed" })));
        var next = message with { EventId = Guid.NewGuid(), TriggerSequence = 2 };
        Assert.True(await consumer.HandleAsync(Envelope(next)));
        Assert.Equal("Accepted", (await sender.QueryAsync(new GetScheduledCostReceipt(next.EventId))).Value.Decision);
        var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "message" && item.Phase == "finished").ToArray();
        Assert.Equal(new[] { "accepted", "duplicate", "rejected", "rejected" }, attempts.Select(item => item.Outcome).Order(StringComparer.Ordinal));
        var phases = await OperationEndpointInventoryTests.ReadAsync(journal);
        var malformed = message with { EventId = Guid.NewGuid(), TargetKind = "unsupported" };
        Assert.False(await consumer.HandleAsync(Envelope(malformed)));
        Assert.True((await sender.QueryAsync(new GetScheduledCostReceipt(malformed.EventId))).IsFailure);
        Assert.Equal(phases.Length, (await OperationEndpointInventoryTests.ReadAsync(journal)).Length);
    }

    private static IIntegrationEventProcessor Processor(IServiceProvider services) => services.GetRequiredKeyedService<IIntegrationEventProcessor>(ScheduleTriggeredV1.Name);

    private static ScheduleTriggeredV1 Trigger(Guid itemId) => new()
    {
        PlanId = 101,
        TriggerSequence = 1,
        ScheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        OccurredAt = DateTimeOffset.UtcNow,
        TargetKind = CostingScheduleTarget.Recalculate,
        TargetId = itemId,
        CreatedBy = "42",
    };

    private static EventEnvelope Envelope(ScheduleTriggeredV1 message) => OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();
}
