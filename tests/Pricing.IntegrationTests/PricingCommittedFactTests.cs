using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingCommittedFactTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task FactFailure_RollsBackManualInputTaskAndResult_AndTheSameScopeCanRetry()
    {
        await using var app = CreateApplication(null);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        await RejectFactsAsync(async () =>
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => sender.SendAsync(command));
            Assert.True((await sender.QueryAsync(new GetPriceQuote(command.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(command.RequestId))).IsFailure);
            Assert.Empty(await ReadFactsAsync(scope.ServiceProvider));
        });
        Assert.True((await sender.SendAsync(command)).IsSuccess);
        var initial = (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value;
        var fee = new UpdatePricingFee(Guid.NewGuid(), command.ItemId, initial.Version, 0.5m);
        await RejectFactsAsync(async () =>
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => sender.SendAsync(fee));
            Assert.Equal(initial, (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
            Assert.True((await sender.QueryAsync(new GetRecalculation(fee.RequestId))).IsFailure);
            Assert.Single(await ReadFactsAsync(scope.ServiceProvider));
        });
        Assert.True((await sender.SendAsync(fee)).IsSuccess);
        var old = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.True((await sender.SendAsync(new CompletePricingWork(old.TaskId, old.Epoch))).Value);
        var lease = (await sender.SendAsync(new ClaimPricingWork())).Value!;
        Assert.Equal(fee.RequestId, lease.TaskId);
        await RejectFactsAsync(async () =>
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch)));
            var unchanged = (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value;
            Assert.Equal(2, unchanged.Version);
            Assert.Null(unchanged.BreakEvenPrice);
            Assert.Equal("Running", (await sender.QueryAsync(new GetRecalculation(lease.TaskId))).Value.State);
            Assert.Equal(2, (await ReadFactsAsync(scope.ServiceProvider)).Length);
        });
        Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        Assert.Equal(160m, (await sender.QueryAsync(new GetPriceQuote(command.ItemId))).Value.BreakEvenPrice);
        Assert.Equal(new[] { "created", "inputs-changed", "result-applied" }, (await ReadFactsAsync(scope.ServiceProvider)).Select(fact => fact.Operation));
    }

    [PostgresFact]
    public async Task ImportedFactFailure_RollsBackInboxAndBusiness_AndSameMessageCanBeRedeliveredInTheSameScope()
    {
        await using var app = CreateApplication(null);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var receiver = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        var item = Guid.NewGuid();
        var first = CostIngestionTests.Cost(item, 1, 80m);
        await RejectFactsAsync(async () =>
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => receiver.HandleAsync(first));
            Assert.True((await sender.QueryAsync(new GetPriceQuote(item))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetRecalculation(first.MessageId))).IsFailure);
            Assert.Empty(await ReadFactsAsync(scope.ServiceProvider));
        });
        Assert.True(await receiver.HandleAsync(first));
        var initial = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        var next = CostIngestionTests.Cost(item, 2, 100m);
        await RejectFactsAsync(async () =>
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => receiver.HandleAsync(next));
            Assert.Equal(initial, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
            Assert.True((await sender.QueryAsync(new GetRecalculation(next.MessageId))).IsFailure);
            Assert.Equal(2, (await ReadFactsAsync(scope.ServiceProvider)).Length);
        });
        Assert.True(await receiver.HandleAsync(next));
        Assert.True(await receiver.HandleAsync(next));
        Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(item))).Value.Cost);
        Assert.Equal(3, (await ReadFactsAsync(scope.ServiceProvider)).Length);
    }

    private async Task RejectFactsAsync(Func<Task> verify)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var install = new NpgsqlCommand("""
            CREATE FUNCTION pricing.reject_committed_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."EventName" = 'pricing.price-quote-committed.v1' THEN RAISE EXCEPTION 'Injected fact failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER reject_committed_fact BEFORE INSERT ON pricing.outbox
            FOR EACH ROW EXECUTE FUNCTION pricing.reject_committed_fact();
            """, connection);
        await install.ExecuteNonQueryAsync();
        try { await verify(); }
        finally
        {
            await using var remove = new NpgsqlCommand("DROP TRIGGER reject_committed_fact ON pricing.outbox; DROP FUNCTION pricing.reject_committed_fact()", connection);
            await remove.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task ManualChangesAndAppliedResults_RecordOnlyActualChanges_WithoutAmounts()
    {
        var command = new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 0.2m);
        await using (var app = CreateApplication("price-editor"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(command)).IsSuccess);
            Assert.True((await sender.SendAsync(command)).IsSuccess);
            Assert.True((await sender.SendAsync(command with { Cost = 81m })).IsFailure);
            Assert.True((await sender.SendAsync(new UpdatePricingFee(Guid.NewGuid(), command.ItemId, 1, 0.2m))).IsSuccess);
            Assert.True((await sender.SendAsync(new UpdatePricingFee(Guid.NewGuid(), command.ItemId, 1, 0.3m))).IsSuccess);
            Assert.True((await sender.SendAsync(command with { RequestId = Guid.NewGuid(), ExpectedVersion = 2, Cost = 90m, FeeRate = 0.3m })).IsSuccess);
            Assert.True((await sender.SendAsync(command with { RequestId = Guid.NewGuid(), ExpectedVersion = 1 })).IsFailure);
            var facts = await ReadFactsAsync(scope.ServiceProvider);
            Assert.Equal(new[] { "created", "inputs-changed", "inputs-changed" }, facts.Select(fact => fact.Operation));
            Assert.Equal(new long[] { 1, 2, 3 }, facts.Select(fact => fact.Version));
            Assert.All(facts, fact => { Assert.Equal("price-editor", fact.ActorId); Assert.Null(fact.CostingItemId); });
        }
        await using var worker = CreateApplication(null);
        await using var work = worker.CreateAsyncScope();
        var workSender = work.ServiceProvider.GetRequiredService<ISender>();
        await CompleteAllAsync(workSender);
        var completed = (await workSender.QueryAsync(new GetPriceQuote(command.ItemId))).Value;
        Assert.Equal(4, completed.Version);
        var all = await ReadFactsAsync(work.ServiceProvider);
        Assert.Equal(4, all.Length);
        Assert.Null(Assert.Single(all, fact => fact.Operation == "result-applied").ActorId);
        Assert.True((await workSender.SendAsync(command with
        { RequestId = Guid.NewGuid(), ExpectedVersion = completed.Version, Cost = completed.Cost, FeeRate = completed.FeeRate })).IsSuccess);
        await CompleteAllAsync(workSender);
        Assert.Equal(completed, (await workSender.QueryAsync(new GetPriceQuote(command.ItemId))).Value);
        Assert.Equal(4, (await ReadFactsAsync(work.ServiceProvider)).Length);
    }

    [PostgresFact]
    public async Task ImportedCostFacts_DistinguishNewSameValueRevisions_FromDuplicateOldAndConflictingMessages()
    {
        var item = Guid.NewGuid();
        var original = CostIngestionTests.Cost(item, 2, 100m);
        await using var app = CreateApplication(null);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var attempt = app.CreateAsyncScope();
            return await attempt.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name).HandleAsync(original);
        }));
        Assert.All(results, Assert.True);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var consumer = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventProcessor>(CostCalculatedV1.Name);
        var initial = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        Assert.True(await consumer.HandleAsync(CostIngestionTests.Cost(item, 1, 80m)));
        Assert.True(await consumer.HandleAsync(CostIngestionTests.Cost(item, 2, 100m)));
        Assert.False(await consumer.HandleAsync(CostIngestionTests.Cost(item, 2, 101m)));
        Assert.Equal(initial, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
        var firstFacts = await ReadFactsAsync(scope.ServiceProvider);
        Assert.Equal(2, firstFacts.Length);
        Assert.All(firstFacts, fact => Assert.Equal(2, fact.Version));
        Assert.Null(Assert.Single(firstFacts, fact => fact.Operation == "created").CostingItemId);
        Assert.Equal(item, Assert.Single(firstFacts, fact => fact.Operation == "costing-applied").CostingItemId);
        Assert.True(await consumer.HandleAsync(CostIngestionTests.Cost(item, 3, 100m)));
        var newer = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        Assert.Equal(initial.InputRevision, newer.InputRevision);
        Assert.Equal(initial.Version + 1, newer.Version);
        Assert.Equal(3, (await ReadFactsAsync(scope.ServiceProvider)).Length);
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, newer.Version, 99m, 0m))).IsFailure);
        Assert.True((await sender.SendAsync(new UpdatePricingFee(Guid.NewGuid(), item, newer.Version, 0.2m))).IsSuccess);
        await CompleteAllAsync(sender);
        var facts = await ReadFactsAsync(scope.ServiceProvider);
        Assert.Equal(5, facts.Length);
        Assert.Equal(new long[] { 2, 2, 3, 4, 5 }, facts.Select(fact => fact.Version));
        Assert.Equal(2, facts.Count(fact => fact.Operation == "costing-applied"));
        Assert.Single(facts, fact => fact.Operation == "inputs-changed");
        Assert.Single(facts, fact => fact.Operation == "result-applied");
        Assert.All(facts, fact => Assert.Null(fact.ActorId));
    }

    private ServiceProvider CreateApplication(string? actor)
    {
        var services = new ServiceCollection();
        services.AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        services.AddPricingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task CompleteAllAsync(ISender sender)
    {
        for (var count = 0; count < 10; count++)
        {
            var lease = (await sender.SendAsync(new ClaimPricingWork())).Value;
            if (lease is null) { return; }
            Assert.True((await sender.SendAsync(new CompletePricingWork(lease.TaskId, lease.Epoch))).Value);
        }
        Assert.Fail("Fixture work did not drain within ten attempts.");
    }

    private static async Task<PriceQuoteCommittedV1[]> ReadFactsAsync(IServiceProvider services)
    {
        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var messages = await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow);
        Assert.All(messages, message =>
        {
            Assert.Equal(PriceQuoteCommittedV1.Name, message.EventName);
            Assert.DoesNotContain("breakEvenPrice", message.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("feeRate", message.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("\"cost\"", message.Payload, StringComparison.Ordinal);
        });
        return messages.Select(message => serializer.Deserialize<PriceQuoteCommittedV1>(message.Payload))
            .OrderBy(fact => fact.Version).ThenBy(fact => fact.Operation, StringComparer.Ordinal).ToArray();
    }
}
