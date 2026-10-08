using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Contracts;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class ScheduledCostingTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task BatchReservedChildIdentity_RejectsScheduledOccupationEvenBeforeTheChildIsRegistered()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), item, 0, 80m, 20m))).IsSuccess);
        var batchId = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new AcceptCostBatch(batchId, [new(1, Guid.NewGuid(), 0, 10m, 20m)]))).IsSuccess);
        var child = Assert.Single((await sender.QueryAsync(new ListCostBatchRows(batchId))).Value.Items).TaskId!.Value;
        var scheduled = Trigger(item) with { EventId = child };
        Assert.True(await Processor(scope).HandleAsync(Envelope(scheduled)));
        Assert.True((await sender.QueryAsync(new GetCostCalculation(child))).IsFailure);
        Assert.True((await sender.SendAsync(new CancelCostBatch(batchId, 0))).IsSuccess);
        Assert.True(await Processor(scope).HandleAsync(Envelope(scheduled)));
        Assert.True((await sender.QueryAsync(new GetCostCalculation(child))).IsFailure);
    }

    [PostgresFact]
    public async Task OccurrenceReplay_CannotChangeRemoveOrBackfillItsExecutionOrigin()
    {
        var id = Guid.NewGuid();
        var origin = new ExecutionOrigin(id, "platform", id, "platform", "42", id.ToString("N"));
        var triggered = Trigger(Guid.NewGuid()) with { ExecutionOrigin = origin };
        var legacy = triggered with { EventId = Guid.NewGuid(), TriggerSequence = 2, ExecutionOrigin = null };
        await using (var application = CreateApplication())
        await using (var scope = application.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new UpdateCostInputs(Guid.NewGuid(), triggered.TargetId, 0, 80m, 20m))).IsSuccess);
            Assert.True(await Processor(scope).HandleAsync(Envelope(triggered)));
            Assert.True(await Processor(scope).HandleAsync(Envelope(legacy)));
        }
        await using var reopened = CreateApplication();
        await using var reopenedScope = reopened.CreateAsyncScope();
        var processor = Processor(reopenedScope);
        Assert.True(await processor.HandleAsync(Envelope(triggered)));
        Assert.True(await processor.HandleAsync(Envelope(legacy)));
        Assert.False(await processor.HandleAsync(Envelope(triggered with { ExecutionOrigin = origin with { InitiatorId = "changed" } })));
        Assert.False(await processor.HandleAsync(Envelope(triggered with { ExecutionOrigin = null })));
        Assert.False(await processor.HandleAsync(Envelope(legacy with { ExecutionOrigin = origin })));
        var sender = reopenedScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(origin, (await sender.QueryAsync(new GetCostCalculation(triggered.EventId))).Value.ExecutionOrigin);
        Assert.Null((await sender.QueryAsync(new GetCostCalculation(legacy.EventId))).Value.ExecutionOrigin);
    }

    [PostgresFact]
    public async Task MalformedOccurrenceOrigin_IsRejectedBeforeReceipt_AndCorrectedDeliveryCanBeAccepted()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var id = Guid.NewGuid();
        var origin = new ExecutionOrigin(id, "platform", id, "platform", "42", id.ToString("N"));
        var triggered = Trigger(Guid.NewGuid()) with { ExecutionOrigin = origin };
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), triggered.TargetId, 0, 80m, 20m))).IsSuccess);
        Assert.False(await Processor(scope).HandleAsync(Envelope(triggered with { ExecutionOrigin = origin with { Source = "unsafe\nsource" } })));
        Assert.True((await sender.QueryAsync(new GetScheduledCostReceipt(triggered.EventId))).IsFailure);
        Assert.True((await sender.QueryAsync(new GetCostCalculation(triggered.EventId))).IsFailure);
        Assert.True(await Processor(scope).HandleAsync(Envelope(triggered)));
        Assert.Equal(origin, (await sender.QueryAsync(new GetCostCalculation(triggered.EventId))).Value.ExecutionOrigin);
    }

    [PostgresFact]
    public async Task OccurrenceIdAlreadyUsedByManualWork_IsStablyRejected_WithoutChangingTheManualRequest()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var message = Trigger(Guid.NewGuid());
        var manual = new UpdateCostInputs(message.EventId, message.TargetId, 0, 80m, 20m);
        Assert.True((await sender.SendAsync(manual)).IsSuccess);
        Assert.True(await Processor(scope).HandleAsync(Envelope(message)));
        var receipt = (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value;
        Assert.Equal("Rejected", receipt.Decision);
        Assert.Equal("costing.schedule_task_conflict", receipt.ErrorCode);
        Assert.Null(receipt.TaskId);
        Assert.True((await sender.SendAsync(manual)).IsSuccess);
        Assert.True(await Processor(scope).HandleAsync(Envelope(message)));
        Assert.Equal(receipt, (await sender.QueryAsync(new GetScheduledCostReceipt(message.EventId))).Value);
    }

    [PostgresFact]
    public async Task MissingTarget_CommitsStableRejection_AndOnlyANewOccurrenceCanBeAcceptedLater()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var processor = Processor(scope);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var triggered = Trigger(Guid.NewGuid());
        Assert.True(await processor.HandleAsync(Envelope(triggered)));
        var rejected = (await sender.QueryAsync(new GetScheduledCostReceipt(triggered.EventId))).Value;
        Assert.Equal("Rejected", rejected.Decision);
        Assert.Equal("costing.schedule_target_not_found", rejected.ErrorCode);
        Assert.Null(rejected.TaskId);
        Assert.True((await sender.QueryAsync(new GetCostCalculation(triggered.EventId))).IsFailure);
        Assert.Equal("costing.request_conflict", (await sender.SendAsync(new UpdateCostInputs(triggered.EventId, triggered.TargetId, 0, 80m, 20m))).Error.Code);

        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), triggered.TargetId, 0, 80m, 20m))).IsSuccess);
        Assert.True(await processor.HandleAsync(Envelope(triggered)));
        Assert.Equal(rejected, (await sender.QueryAsync(new GetScheduledCostReceipt(triggered.EventId))).Value);
        Assert.False(await processor.HandleAsync(Envelope(triggered with { CreatedBy = "forged" })));
        var next = triggered with { EventId = Guid.NewGuid(), TriggerSequence = 2 };
        Assert.True(await processor.HandleAsync(Envelope(next)));
        Assert.Equal("Accepted", (await sender.QueryAsync(new GetScheduledCostReceipt(next.EventId))).Value.Decision);
    }

    [PostgresFact]
    public async Task ScheduledRecalculation_UsesCurrentLocalInput_AndDuplicateDeliveryCreatesOneTask()
    {
        await using var app = CreateApplication();
        var itemId = Guid.NewGuid();
        CostSheetView original;
        await using (var setup = app.CreateAsyncScope())
        {
            var sender = setup.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), itemId, 0, 80m, 20m))).IsSuccess);
            var initial = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            Assert.True((await sender.SendAsync(new CompleteCostingWork(initial.TaskId, initial.Epoch))).Value);
            original = (await sender.QueryAsync(new GetCostSheet(itemId))).Value;
        }
        var triggered = Trigger(itemId);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return await Processor(scope).HandleAsync(Envelope(triggered));
        }));
        Assert.All(accepted, Assert.True);
        await using var check = app.CreateAsyncScope();
        var query = check.ServiceProvider.GetRequiredService<ISender>();
        var receipt = (await query.QueryAsync(new GetScheduledCostReceipt(triggered.EventId))).Value;
        Assert.Equal("Accepted", receipt.Decision);
        Assert.Equal(triggered.EventId, receipt.TaskId);
        Assert.Equal(itemId, receipt.ItemId);
        Assert.Equal("42", receipt.CreatedBy);
        var lease = (await query.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(triggered.EventId, lease.TaskId);
        Assert.True((await query.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Null((await query.SendAsync(new ClaimCostingWork())).Value);
        var cost = (await query.QueryAsync(new GetCostSheet(itemId))).Value;
        Assert.Equal(100m, cost.UnitCost);
        Assert.Equal(original.Version, cost.Version);
        Assert.NotNull(cost.Audit);
        Assert.Equal(original.Audit, cost.Audit);
        Assert.Null(cost.Audit.CreatedBy);
        Assert.Null(cost.Audit.UpdatedBy);
        Assert.Equal(1, cost.InputRevision);
        Assert.Equal("Succeeded", (await query.QueryAsync(new GetCostCalculation(triggered.EventId))).Value.State);
    }

    private ServiceProvider CreateApplication()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static IIntegrationEventProcessor Processor(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(ScheduleTriggeredV1.Name);

    private static ScheduleTriggeredV1 Trigger(Guid itemId) => new()
    {
        EventId = Guid.NewGuid(),
        PlanId = 101,
        TriggerSequence = 1,
        ScheduledAt = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        OccurredAt = new DateTimeOffset(2026, 10, 2, 9, 0, 10, TimeSpan.Zero),
        TargetKind = "costing.recalculate",
        TargetId = itemId,
        CreatedBy = "42",
    };

    private static EventEnvelope Envelope(ScheduleTriggeredV1 triggered) => new()
    {
        MessageId = triggered.EventId,
        EventName = triggered.EventName,
        OccurredAt = triggered.OccurredAt,
        Payload = new SystemTextJsonIntegrationEventSerializer().Serialize(triggered),
    };
}
