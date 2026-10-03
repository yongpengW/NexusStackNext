using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingWorkflowTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider CreateApplication(CostingTaskOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString, options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task Completion_CommitsCostAndItsVersionedEventTogether()
    {
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        await using (var app = CreateApplication())
        {
            await using var scope = app.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(request)).IsSuccess);
            var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        }

        await using var reopened = CreateApplication();
        await using var read = reopened.CreateAsyncScope();
        var query = read.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(100m, (await query.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
        var envelope = Assert.Single(await read.ServiceProvider.GetRequiredService<IOutboxStore>()
            .ReadPendingAsync(10, DateTimeOffset.UtcNow), entry => entry.EventName == CostCalculatedV1.Name);
        Assert.Equal(request.RequestId, envelope.Id);
        Assert.Equal(CostCalculatedV1.Name, envelope.EventName);
        var store = read.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.MarkDeliveredAsync(envelope.Id, DateTimeOffset.UtcNow);
        Assert.Equal("Delivered", (await query.QueryAsync(new GetCostDelivery(request.RequestId))).Value.State);
        // 先前失败的投递器晚回来，不能把已经确认的消息改成失败。
        await store.MarkDeadLetteredAsync(envelope.Id, "late_failure", DateTimeOffset.UtcNow, 0);
        Assert.Equal("Delivered", (await query.QueryAsync(new GetCostDelivery(request.RequestId))).Value.State);
    }

    [PostgresFact]
    public async Task OutboxFailure_RollsBackCostAndTask_ThenSameLeaseCanRecover()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        await database.RejectOutboxWriteAsync(request.RequestId, true);
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch)));
        Assert.Null((await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
        Assert.Equal("Running", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.State);
        Assert.True((await sender.QueryAsync(new GetCostDelivery(request.RequestId))).IsFailure);
        await database.RejectOutboxWriteAsync(request.RequestId, false);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal("Pending", (await sender.QueryAsync(new GetCostDelivery(request.RequestId))).Value.State);
    }

    [PostgresFact]
    public async Task DuplicateRegistration_AndExpiredLease_KeepOneRecoverableWorkHistory()
    {
        await using var app = CreateApplication(new CostingTaskOptions { LeaseDuration = TimeSpan.FromMilliseconds(800), MaxAttempts = 1 });
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(request);
        }));
        Assert.All(accepted, x => Assert.Equal(request.RequestId, x.Value.TaskId));
        await using var check = app.CreateAsyncScope();
        var sender = check.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("costing.request_conflict", (await sender.SendAsync(request with { PurchaseCost = 90m })).Error.Code);
        var expired = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Null((await sender.SendAsync(new ClaimCostingWork())).Value);
        await Task.Delay(1000);
        Assert.False((await sender.SendAsync(new CompleteCostingWork(expired.TaskId, expired.Epoch))).Value);
        Assert.False((await sender.SendAsync(new FailCostingWork(expired.TaskId, expired.Epoch))).Value);
        Assert.Null((await sender.SendAsync(new ClaimCostingWork())).Value);
        Assert.Equal("Failed", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.State);
        Assert.True((await sender.SendAsync(new RetryCostingWork(request.RequestId, expired.Epoch))).IsSuccess);
        // 故障注入只缩短原执行者的租约；健康恢复使用正常预算，避免把网络延迟当成再次故障。
        await using var recoveryApp = CreateApplication(new CostingTaskOptions { MaxAttempts = 1 });
        await using var recoveryScope = recoveryApp.CreateAsyncScope();
        var recoverySender = recoveryScope.ServiceProvider.GetRequiredService<ISender>();
        var recovered = (await recoverySender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(2, recovered.Epoch);
        Assert.True((await recoverySender.SendAsync(new CompleteCostingWork(recovered.TaskId, recovered.Epoch))).Value);
        Assert.Equal(new[] { "Expired", "Succeeded" }, (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.History.Select(x => x.Outcome));
    }

    [PostgresFact]
    public async Task SupersededCalculation_PublishesNoOldCost_AndDeliveryCanBeManuallyRetried()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var original = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Assert.True((await sender.SendAsync(original)).IsSuccess);
        var oldLease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        var current = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 90m };
        Assert.True((await sender.SendAsync(current)).IsSuccess);
        var currentLease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompleteCostingWork(currentLease.TaskId, currentLease.Epoch))).Value);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(oldLease.TaskId, oldLease.Epoch))).Value);
        Assert.Equal("Superseded", (await sender.QueryAsync(new GetCostCalculation(original.RequestId))).Value.State);
        Assert.Equal("costing.cancel_conflict", (await sender.SendAsync(new CancelCostingWork(oldLease.TaskId, oldLease.Epoch))).Error.Code);
        Assert.Equal("costing.renew_conflict", (await sender.SendAsync(new RenewCostingWork(oldLease.TaskId, oldLease.Epoch))).Error.Code);
        Assert.True((await sender.QueryAsync(new GetCostDelivery(original.RequestId))).IsFailure);
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.MarkDeadLetteredAsync(current.RequestId, "broker_unavailable", DateTimeOffset.UtcNow, 0);
        var dead = (await sender.QueryAsync(new GetCostDelivery(current.RequestId))).Value;
        Assert.Equal("DeadLettered", dead.State);
        var retry = new RetryCostDelivery(current.RequestId, dead.DeadLetteredAt!.Value);
        Assert.Equal("Pending", (await sender.SendAsync(retry)).Value.State);
        Assert.Equal("costing.delivery_conflict", (await sender.SendAsync(retry)).Error.Code);
        await store.MarkDeadLetteredAsync(current.RequestId, "old publisher failure", DateTimeOffset.UtcNow, 0);
        Assert.Equal("Pending", (await sender.QueryAsync(new GetCostDelivery(current.RequestId))).Value.State);
        Assert.Equal(current.RequestId, Assert.Single(await store.ReadPendingAsync(10, DateTimeOffset.UtcNow), entry => entry.EventName == CostCalculatedV1.Name).Id);
    }
}
