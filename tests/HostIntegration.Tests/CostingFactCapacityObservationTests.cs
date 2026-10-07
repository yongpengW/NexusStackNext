using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class CostingFactCapacityObservationTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task CapacityRefusal_IsObservedAsFailed_AndSameLeaseRecoveryIsASeparateCompletedAttempt()
    {
        await using var database = await databases.CreateAsync("costing");
        await SetQuotaAsync(database.ConnectionString, 1);
        await using var app = TaskOperationTests.CreateCostingApp(database.ConnectionString, "cost-capacity-initiator");
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        var accepted = (await sender.SendAsync(request)).Value;
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal("costing.audit_capacity_exhausted", (await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Error.Code);
        var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
        var refused = Assert.Single(await OperationEndpointInventoryTests.ReadAsync(journal), item => item.Kind == "task" && item.Phase == "finished");
        Assert.Equal("failed", refused.Outcome);
        Assert.Equal("Running", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.State);
        await SetQuotaAsync(database.ConnectionString, 2);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
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
            Assert.Equal("cost-capacity-initiator", attempt.Metadata.InitiatorId);
        }
        Assert.Equal("Succeeded", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.State);
    }

    private static async Task SetQuotaAsync(string connectionString, long limit)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE costing.fact_capacity SET \"MaxRecords\" = @limit", connection);
        command.Parameters.AddWithValue("limit", limit);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
