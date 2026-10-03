using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PricingFactCapacityObservationTests
{
    [PostgresFact]
    public async Task MessageCapacityRefusal_IsObservedAsFailed_AndRecoveryIsASeparateAcceptedAttempt()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        await SetQuotaAsync(database.ConnectionString, 1);
        await using var app = TaskOperationTests.CreatePricingApp(database.ConnectionString, null);
        await using var scope = app.Services.CreateAsyncScope();
        var operation = Guid.NewGuid();
        var origin = new ExecutionOrigin(operation, "costing", operation, "costing", "upstream-capacity-initiator", operation.ToString("N"));
        var message = new CostCalculatedV1
        {
            EventId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            CostRevision = 1,
            UnitCost = 100m,
            OccurredAt = DateTimeOffset.UtcNow,
            ExecutionOrigin = origin,
        };
        var envelope = OutboxEntry.From(message, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();
        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        Assert.False(await processor.HandleAsync(envelope));
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var refused = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Kind == "message" && item.Phase == "finished");
        Assert.Equal("failed", refused.Outcome);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await sender.QueryAsync(new GetRecalculation(message.EventId))).IsFailure);
        await SetQuotaAsync(database.ConnectionString, 2);
        Assert.True(await processor.HandleAsync(envelope));
        Assert.True(await processor.HandleAsync(envelope));
        var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "message" && item.Phase == "finished").ToArray();
        Assert.Equal(3, attempts.Length);
        var accepted = Assert.Single(attempts, item => item.Outcome == "accepted");
        Assert.Single(attempts, item => item.Outcome == "duplicate");
        Assert.Equal(3, attempts.Select(item => item.OperationId).Distinct().Count());
        var taskOrigin = (await sender.QueryAsync(new GetRecalculation(message.EventId))).Value.ExecutionOrigin!;
        Assert.Equal(accepted.OperationId, taskOrigin.OperationId);
        foreach (var attempt in attempts)
        {
            Assert.Null(attempt.ActorId);
            Assert.Equal(origin.OperationId, attempt.Metadata!.ParentOperationId);
            Assert.Equal(origin.RootOperationId, attempt.Metadata.RootOperationId);
            Assert.Equal(origin.InitiatorId, attempt.Metadata.InitiatorId);
        }
    }

    [PostgresFact]
    public async Task TaskCapacityRefusal_IsObservedAsFailed_AndSameLeaseRecoveryIsASeparateCompletedAttempt()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        await SetQuotaAsync(database.ConnectionString, 1);
        await using var app = TaskOperationTests.CreatePricingApp(database.ConnectionString, "price-capacity-initiator",
            new PricingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(60) });
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        var accepted = (await sender.SendAsync(request)).Value;
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal("pricing.audit_capacity_exhausted", (await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Error.Code);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var refused = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Kind == "task" && item.Phase == "finished");
        Assert.Equal("failed", refused.Outcome);
        Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.State);
        await SetQuotaAsync(database.ConnectionString, 2);
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        var attempts = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "task" && item.Phase == "finished").ToArray();
        var completed = Assert.Single(attempts, item => item.Outcome == "completed");
        Assert.Equal(2, attempts.Length);
        Assert.NotEqual(refused.OperationId, completed.OperationId);
        foreach (var attempt in attempts)
        {
            Assert.Null(attempt.ActorId);
            Assert.Equal(request.RequestId, attempt.Metadata!.TaskId);
            Assert.Equal(lease.Epoch, attempt.Metadata.TaskEpoch);
            Assert.Equal(accepted.ExecutionOrigin!.RootOperationId, attempt.Metadata.RootOperationId);
            Assert.Equal("price-capacity-initiator", attempt.Metadata.InitiatorId);
        }
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetRecalculation(request.RequestId))).Value.State);
    }

    private static async Task SetQuotaAsync(string connectionString, long limit)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE pricing.fact_capacity SET \"MaxRecords\" = @limit", connection);
        command.Parameters.AddWithValue("limit", limit);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
