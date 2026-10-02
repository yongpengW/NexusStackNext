using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class CostIngestionTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private ServiceProvider CreateApplication()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddPricingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    internal static EventEnvelope Cost(Guid item, long revision, decimal cost, Guid? messageId = null) =>
        OutboxEntry.From(new CostCalculatedV1
        {
            EventId = messageId ?? Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            ItemId = item,
            CostRevision = revision,
            UnitCost = cost,
        }, new SystemTextJsonIntegrationEventSerializer()).ToEnvelope();

    [PostgresFact]
    public async Task CostEvent_RegistersDurableRecalculation_PreservingPricingFee()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 50m, 0.2m))).IsSuccess);
        var envelope = Cost(item, 2, 80m);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(envelope));
        Assert.Equal("Pending", (await sender.QueryAsync(new GetRecalculation(envelope.MessageId))).Value.State);
        var quote = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        Assert.Equal(80m, quote.Cost);
        Assert.Equal(0.2m, quote.FeeRate);
        Assert.Equal(2, quote.CostingRevision);
    }

    [PostgresFact]
    public async Task ConcurrentDuplicateAndOlderEvents_DoNotAdvanceTheQuoteOrRegisterMoreWork()
    {
        await using var app = CreateApplication();
        var item = Guid.NewGuid();
        var latest = Cost(item, 3, 120m);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(latest);
        }));
        Assert.All(results, Assert.True);
        await using var read = app.CreateAsyncScope();
        var sender = read.ServiceProvider.GetRequiredService<ISender>();
        var before = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        var processor = read.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
        Assert.True(await processor.HandleAsync(Cost(item, 1, 80m)));
        Assert.True(await processor.HandleAsync(Cost(item, 3, 120m)));
        Assert.False(await processor.HandleAsync(Cost(item, 3, 121m)));
        Assert.Equal(before, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(latest.MessageId, lease.TaskId);
        Assert.Null((await sender.SendAsync(new ClaimPricingWork())).Value);
    }

    [PostgresFact]
    public async Task ReusedMessageIdentity_RejectsChangedContent_EvenWhenOriginalRevisionWasIgnored()
    {
        var item = Guid.NewGuid();
        var latest = Cost(item, 3, 120m);
        var ignored = Cost(item, 1, 80m);
        await using (var app = CreateApplication())
        await using (var scope = app.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
            Assert.True(await processor.HandleAsync(latest));
            Assert.True(await processor.HandleAsync(ignored));
        }
        await using var reopened = CreateApplication();
        await using var read = reopened.CreateAsyncScope();
        var receiver = read.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
        Assert.True(await receiver.HandleAsync(latest));
        Assert.True(await receiver.HandleAsync(ignored));
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var originalCost = serializer.Deserialize<CostCalculatedV1>(latest.Payload);
        var ignoredCost = serializer.Deserialize<CostCalculatedV1>(ignored.Payload);
        // 保持原时间和消息身份，只改变业务字段；数值表示不同但值相同仍是重复。
        Assert.True(await receiver.HandleAsync(OutboxEntry.From(originalCost with { UnitCost = 120.0000m }, serializer).ToEnvelope()));
        Assert.False(await receiver.HandleAsync(OutboxEntry.From(originalCost with { CostRevision = 4, UnitCost = 140m }, serializer).ToEnvelope()));
        Assert.False(await receiver.HandleAsync(OutboxEntry.From(ignoredCost with { ItemId = Guid.NewGuid() }, serializer).ToEnvelope()));
        Assert.Equal(120m, (await read.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(item))).Value.Cost);
    }

    [PostgresFact]
    public async Task TaskRegistrationFailure_RollsBackInboxAndCost_SameMessageCanBeRedelivered()
    {
        var item = Guid.NewGuid();
        var message = Cost(item, 1, 100m);
        await database.RejectTaskInsertAsync(message.MessageId);
        await using (var app = CreateApplication())
        {
            await using var scope = app.CreateAsyncScope();
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
                scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(message));
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.QueryAsync(new GetPriceQuote(item))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(message.MessageId))).IsFailure);
        }
        await database.AllowTaskInsertsAsync();
        await using var reopened = CreateApplication();
        await using var read = reopened.CreateAsyncScope();
        Assert.True(await read.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>().HandleAsync(message));
        Assert.Equal(100m, (await read.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetPriceQuote(item))).Value.Cost);
    }

    [PostgresFact]
    public async Task FeeChanges_RemainLocal_AndManualCostCannotOverwriteAnImportedCost()
    {
        await using var app = CreateApplication();
        await using var scope = app.CreateAsyncScope();
        var item = Guid.NewGuid();
        var processor = scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True(await processor.HandleAsync(Cost(item, 1, 80m)));
        var quote = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        Assert.Equal("pricing.cost_owned_by_costing", (await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, quote.Version, 1m, 0.1m))).Error.Code);
        var fee = new UpdatePricingFee(Guid.NewGuid(), item, quote.Version, 0.2m);
        Assert.True((await sender.SendAsync(fee)).IsSuccess);
        Assert.True((await sender.SendAsync(fee)).IsSuccess);
        Assert.Equal("pricing.request_conflict", (await sender.SendAsync(fee with { FeeRate = 0.3m })).Error.Code);
        Assert.True(await processor.HandleAsync(Cost(item, 2, 120m)));
        var latest = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        Assert.Equal(120m, latest.Cost);
        Assert.Equal(0.2m, latest.FeeRate);
        Assert.Equal(2, latest.CostingRevision);
    }
}
