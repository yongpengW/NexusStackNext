using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingCommittedFactTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task CostDeliveryQueryAndRetry_DoNotTreatAuditMessagesAsCalculatedResults()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        await using var app = CreateApplication(clock, "cost-editor");
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.True((await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m))).IsSuccess);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var fact = Assert.Single(await outbox.ReadPendingAsync(100, clock.UtcNow), entry => entry.EventName == CostSheetCommittedV1.Name);
        await outbox.MarkDeadLetteredAsync(fact.Id, "audit-delivery-failed", clock.UtcNow, 0);
        var query = await sender.QueryAsync(new GetCostDelivery(fact.Id));
        Assert.True(query.IsFailure);
        Assert.Equal("costing.not_found", query.Error.Code);
        var retry = await sender.SendAsync(new RetryCostDelivery(fact.Id, clock.UtcNow));
        Assert.True(retry.IsFailure);
        Assert.Equal("costing.delivery_conflict", retry.Error.Code);
        Assert.DoesNotContain(await outbox.ReadPendingAsync(100, clock.UtcNow), entry => entry.Id == fact.Id);
    }

    [PostgresFact]
    public async Task FactWriteFailure_RollsBackInputsTasksAndResults_ThenTheSameScopeCanRecover()
    {
        await using var app = CreateApplication(new FixedClock(DateTimeOffset.UtcNow), null);
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var original = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        await RejectFactsAsync(true);
        try
        {
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(original));
            Assert.True((await sender.QueryAsync(new GetCostSheet(original.ItemId))).IsFailure);
            Assert.True((await sender.QueryAsync(new GetCostCalculation(original.RequestId))).IsFailure);
            Assert.Empty(await ReadFactsAsync(scope.ServiceProvider));
        }
        finally { await RejectFactsAsync(false); }
        Assert.True((await sender.SendAsync(original)).IsSuccess);
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        var update = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 90m };
        await RejectFactsAsync(true);
        try
        {
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(update));
            Assert.Equal(1, (await sender.QueryAsync(new GetCostSheet(original.ItemId))).Value.Version);
            Assert.True((await sender.QueryAsync(new GetCostCalculation(update.RequestId))).IsFailure);
            Assert.Single(await ReadFactsAsync(scope.ServiceProvider));
        }
        finally { await RejectFactsAsync(false); }
        Assert.True((await sender.SendAsync(update)).IsSuccess);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        var current = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        await RejectFactsAsync(true);
        try
        {
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => sender.SendAsync(new CompleteCostingWork(current.TaskId, current.Epoch)));
            var unchanged = (await sender.QueryAsync(new GetCostSheet(original.ItemId))).Value;
            Assert.Equal(2, unchanged.Version);
            Assert.Null(unchanged.UnitCost);
            Assert.Equal("Running", (await sender.QueryAsync(new GetCostCalculation(current.TaskId))).Value.State);
            Assert.True((await sender.QueryAsync(new GetCostDelivery(current.TaskId))).IsFailure);
            Assert.Equal(2, (await ReadFactsAsync(scope.ServiceProvider)).Length);
        }
        finally { await RejectFactsAsync(false); }
        Assert.True((await sender.SendAsync(new CompleteCostingWork(current.TaskId, current.Epoch))).Value);
        Assert.Equal(110m, (await sender.QueryAsync(new GetCostSheet(original.ItemId))).Value.UnitCost);
        Assert.Equal(new[] { "created", "inputs-changed", "result-applied" },
            (await ReadFactsAsync(scope.ServiceProvider)).Select(fact => fact.GetProperty("operation").GetString()));
    }

    [PostgresFact]
    public async Task InputsAndAppliedResults_CommitMinimalFacts_WhileRepeatedRejectedAndSupersededWorkAddsNothing()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 3, 3, 0, 0, TimeSpan.Zero));
        var original = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        var update = original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, PurchaseCost = 90m };
        await using (var app = CreateApplication(clock, "cost-editor"))
        await using (var scope = app.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            Assert.True((await sender.SendAsync(original)).IsSuccess);
            Assert.True((await sender.SendAsync(original with { PurchaseCost = 81m })).IsFailure);
            Assert.True((await sender.SendAsync(original with { RequestId = Guid.NewGuid(), ExpectedVersion = 1 })).IsSuccess);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await sender.SendAsync(update)).IsSuccess);
            Assert.True((await sender.SendAsync(update with { RequestId = Guid.NewGuid() })).IsFailure);
            var facts = await ReadFactsAsync(scope.ServiceProvider);
            Assert.Equal(new[] { "created", "inputs-changed" }, facts.Select(fact => fact.GetProperty("operation").GetString()));
            Assert.Equal(new long[] { 1, 2 }, facts.Select(fact => fact.GetProperty("version").GetInt64()));
            Assert.All(facts, fact => Assert.Equal("cost-editor", fact.GetProperty("actorId").GetString()));
            Assert.Equal(clock.UtcNow, facts[1].GetProperty("occurredAt").GetDateTimeOffset());
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        await using var worker = CreateApplication(clock, null);
        await using var workScope = worker.CreateAsyncScope();
        var work = workScope.ServiceProvider.GetRequiredService<ISender>();
        var attempts = 0;
        while ((await work.SendAsync(new ClaimCostingWork())).Value is { } lease)
        {
            Assert.True(++attempts <= 3, "只能执行已登记的三项工作。");
            Assert.True((await work.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
            Assert.False((await work.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch))).Value);
        }
        Assert.Equal(3, attempts);
        Assert.Equal("Superseded", (await work.QueryAsync(new GetCostCalculation(original.RequestId))).Value.State);
        var sheet = (await work.QueryAsync(new GetCostSheet(original.ItemId))).Value;
        Assert.Equal(110m, sheet.UnitCost);
        Assert.Equal(3, sheet.Version);
        Assert.True((await work.SendAsync(update with { RequestId = Guid.NewGuid(), ExpectedVersion = sheet.Version })).IsSuccess);
        var repeated = (await work.SendAsync(new ClaimCostingWork())).Value!;
        Assert.True((await work.SendAsync(new CompleteCostingWork(repeated.TaskId, repeated.Epoch))).Value);
        Assert.Equal(sheet, (await work.QueryAsync(new GetCostSheet(original.ItemId))).Value);
        var committed = await ReadFactsAsync(workScope.ServiceProvider);
        Assert.Equal(new[] { "created", "inputs-changed", "result-applied" }, committed.Select(fact => fact.GetProperty("operation").GetString()));
        Assert.Equal(new long[] { 1, 2, 3 }, committed.Select(fact => fact.GetProperty("version").GetInt64()));
        Assert.Equal(JsonValueKind.Null, committed[2].GetProperty("actorId").ValueKind);
        Assert.All(committed, fact =>
        {
            Assert.Equal(original.ItemId, fact.GetProperty("itemId").GetGuid());
            Assert.False(fact.TryGetProperty("purchaseCost", out _));
            Assert.False(fact.TryGetProperty("freightCost", out _));
            Assert.False(fact.TryGetProperty("unitCost", out _));
        });
        var pending = await workScope.ServiceProvider.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, clock.UtcNow);
        Assert.Equal(2, pending.Count(entry => entry.EventName == CostCalculatedV1.Name));
    }

    private ServiceProvider CreateApplication(IClock clock, string? actor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddSingleton(clock);
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));
        services.AddCostingPostgres(database.ConnectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private async Task RejectFactsAsync(bool reject)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var sql = reject ? """
            CREATE OR REPLACE FUNCTION costing.reject_committed_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."EventName" = 'costing.cost-sheet-committed.v1' THEN RAISE EXCEPTION 'injected fact failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE OR REPLACE TRIGGER reject_committed_fact BEFORE INSERT ON costing.outbox
            FOR EACH ROW EXECUTE FUNCTION costing.reject_committed_fact();
            """ : "DROP TRIGGER IF EXISTS reject_committed_fact ON costing.outbox";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement[]> ReadFactsAsync(IServiceProvider services)
    {
        var pending = await services.GetRequiredService<IOutboxStore>().ReadPendingAsync(100, DateTimeOffset.UtcNow);
        return pending.Where(entry => entry.EventName == "costing.cost-sheet-committed.v1").Select(entry =>
        {
            using var document = JsonDocument.Parse(entry.Payload);
            return document.RootElement.Clone();
        }).OrderBy(fact => fact.GetProperty("version").GetInt64()).ToArray();
    }
}
