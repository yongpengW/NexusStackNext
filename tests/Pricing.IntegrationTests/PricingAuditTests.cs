using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingAuditTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Audit_FollowsCommittedChangesAcrossScopes_AndWorkersDoNotImpersonateTheCreator()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        var clock = new MutableClock(createdAt);
        await using (var app = CreateApplication(clock, "creator"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(command)).IsSuccess);
            Assert.Equal(new EntityAuditMetadata(createdAt, "creator", null, null),
                (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.Audit);
        }

        clock.Advance(TimeSpan.FromHours(1));
        PriceQuoteView edited;
        await using (var app = CreateApplication(clock, "editor"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var fee = new UpdatePricingFee(Guid.NewGuid(), command.ItemId, 1, 0.5m);
            Assert.True((await sender.SendAsync(fee)).IsSuccess);
            edited = (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value;
            Assert.Equal(new EntityAuditMetadata(createdAt, "creator", clock.UtcNow, "editor"), edited.Audit);

            clock.Advance(TimeSpan.FromHours(1));
            Assert.True((await sender.SendAsync(fee)).IsSuccess);
            Assert.True((await sender.SendAsync(fee with { RequestId = Guid.NewGuid(), ExpectedVersion = edited.Version })).IsSuccess);
            Assert.Equal(edited, (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
            var failed = command with { RequestId = Guid.NewGuid(), ExpectedVersion = edited.Version, Cost = 120m, FeeRate = 0.5m };
            await database.RejectTaskInsertAsync(failed.RequestId);
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(failed));
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using var worker = CreateApplication(clock, null);
        await using var workScope = worker.CreateAsyncScope();
        var work = workScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(edited, (await work.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
        var superseded = (await work.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await work.SendAsync(new CompletePricingWork(superseded.TaskId, superseded.Epoch))).Value);
        Assert.Equal(edited, (await work.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
        var lease = (await work.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await work.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        var completed = (await work.QueryAsync(new GetPriceQuote(command.ItemId))).Value;
        Assert.Equal(160m, completed.BreakEvenPrice);
        Assert.Equal(new EntityAuditMetadata(createdAt, "creator", clock.UtcNow, null), completed.Audit);

        clock.Advance(TimeSpan.FromHours(1));
        var unchanged = (await work.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await work.SendAsync(new CompletePricingWork(unchanged.TaskId, unchanged.Epoch))).Value);
        Assert.Equal(completed, (await work.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
    }

    [PostgresFact]
    public async Task IntegrationCost_AuditsSystemWrites_AndRejectedOrOldEventsLeaveAuditUnchanged()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        var item = Guid.NewGuid();
        PriceQuoteView initial;
        await using (var app = CreateApplication(clock, null))
        await using (var scope = app.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
            Assert.True(await processor.HandleAsync(CostIngestionTests.Cost(item, 2, 80m)));
            initial = (await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(item))).Value;
            Assert.Equal(new EntityAuditMetadata(clock.UtcNow, null, null, null), initial.Audit);
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using var reopened = CreateApplication(clock, null);
        await using var read = reopened.CreateAsyncScope();
        var receiver = read.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        var sender = read.ServiceProvider.GetRequiredService<ISender>();
        Assert.True(await receiver.HandleAsync(CostIngestionTests.Cost(item, 1, 50m)));
        Assert.False(await receiver.HandleAsync(CostIngestionTests.Cost(item, 2, 81m)));
        Assert.Equal(initial, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
        Assert.True(await receiver.HandleAsync(CostIngestionTests.Cost(item, 3, 100m)));
        Assert.Equal(new EntityAuditMetadata(initial.Audit!.CreatedAt, null, clock.UtcNow, null),
            (await sender.QueryAsync(new GetPriceQuote(item))).Value.Audit);
    }

    private ServiceProvider CreateApplication(IClock clock, string? actor)
    {
        var services = new ServiceCollection();
        services.AddNexusStackApplication();
        services.AddSingleton(clock);
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        services.AddPricingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
