using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyTransactionTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task PolicyAdjustment_CommitsItsOwnTransaction_WhenCallerAmbientTransactionRollsBack()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var policies = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyStore>("files");
        var initial = (await policies.ReadPolicyAsync()).Value;
        var request = new FactCapacityPolicyRequest(Guid.NewGuid(), 1, initial.MaxRecords + 1,
            initial.MaxPayloadBytes, initial.MaxRecordPayloadBytes, "operator-adjustment");
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        FactCapacityPolicyReceipt receipt;
        using (var callerTransaction = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            Assert.NotNull(Transaction.Current);
            Assert.Equal(1, (await policies.ReadPolicyAsync()).Value.PolicyRevision);
            var accepted = await policies.AdjustAsync(request, "test-operator", now, null);
            Assert.True(accepted.IsSuccess);
            receipt = accepted.Value;
            Assert.Equal(2, receipt.PolicyRevision);
            // Deliberately omit Complete: the caller rolls back, while the owned policy transaction must survive.
        }
        Assert.Null(Transaction.Current);
        var after = (await policies.ReadPolicyAsync()).Value;
        Assert.Equal(2, after.PolicyRevision);
        Assert.Equal(initial.MaxRecords + 1, after.MaxRecords);
        Assert.Equal(initial.RetainedRecords, after.RetainedRecords);
        Assert.Equal(1, after.ControlCapacity.RetainedRecords);
        Assert.Equal(receipt, (await policies.AdjustAsync(request, "test-operator", now.AddMinutes(1), null)).Value);
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var fact = Assert.Single(await outbox.ReadPendingAsync(10, DateTimeOffset.MaxValue));
        Assert.Equal(receipt.EventId, fact.Id);
        Assert.Equal("files.fact-capacity-policy-changed.v1", fact.EventName);
    }
}
