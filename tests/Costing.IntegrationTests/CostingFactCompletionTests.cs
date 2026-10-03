using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingFactCompletionTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public Task CanceledFactWrite_DoesNotCommitResultOrMessages_AndNewLeaseCanRecover() => AssertInterruptedCompletionAsync(cancel: true);

    [PostgresFact]
    public Task LeaseExpiresDuringSave_DoesNotCommitResultOrMessages_AndNewLeaseCanRecover() => AssertInterruptedCompletionAsync(cancel: false);

    private async Task AssertInterruptedCompletionAsync(bool cancel)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddCostingPostgres(database.ConnectionString, new CostingTaskOptions { LeaseDuration = TimeSpan.FromSeconds(8) });
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 80m, 20m);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        await using var barrier = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        await barrier.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            SELECT pg_advisory_lock(640064);
            CREATE FUNCTION costing.pause_result_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."EventName" = 'costing.cost-sheet-committed.v1' AND NEW."Payload"::jsonb ->> 'operation' = 'result-applied'
              THEN PERFORM pg_advisory_xact_lock(640064); END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER pause_result_fact BEFORE INSERT ON costing.outbox
            FOR EACH ROW EXECUTE FUNCTION costing.pause_result_fact();
            """, barrier))
        {
            await install.ExecuteNonQueryAsync();
        }
        var lease = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        using var cancellation = new CancellationTokenSource();
        var pending = sender.SendAsync(new CompleteCostingWork(lease.TaskId, lease.Epoch), cancellation.Token);
        var released = false;
        async Task ReleaseAsync()
        {
            if (released) { return; }
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(640064)", barrier);
            Assert.True((bool)(await release.ExecuteScalarAsync())!);
            released = true;
        }
        async Task WaitForExpiryAsync()
        {
            await using var expires = new NpgsqlCommand("SELECT \"LeaseUntil\" <= clock_timestamp() FROM costing.tasks WHERE \"TaskId\" = @task", barrier);
            expires.Parameters.AddWithValue("task", lease.TaskId);
            await WaitForTrueAsync(expires);
        }
        try
        {
            await using var blocked = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND @barrier = ANY(pg_blocking_pids(pid)))", barrier);
            blocked.Parameters.AddWithValue("barrier", barrier.ProcessID);
            await WaitForTrueAsync(blocked);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                await WaitForExpiryAsync();
                await ReleaseAsync();
                Assert.False((await pending).Value);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await ReleaseAsync();
            try { await pending; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await using var remove = new NpgsqlCommand("DROP TRIGGER pause_result_fact ON costing.outbox; DROP FUNCTION costing.pause_result_fact()", barrier);
            await remove.ExecuteNonQueryAsync();
        }
        var unchanged = (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value;
        Assert.Equal(1, unchanged.Version);
        Assert.Null(unchanged.UnitCost);
        Assert.Equal("Running", (await sender.QueryAsync(new GetCostCalculation(request.RequestId))).Value.State);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var existing = Assert.Single(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
        Assert.Equal(CostSheetCommittedV1.Name, existing.EventName);
        Assert.Equal("created", scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>().Deserialize<CostSheetCommittedV1>(existing.Payload).Operation);
        await WaitForExpiryAsync();
        var retry = (await sender.SendAsync(new ClaimCostingWork())).Value!;
        Assert.Equal(lease.Epoch + 1, retry.Epoch);
        Assert.True((await sender.SendAsync(new CompleteCostingWork(retry.TaskId, retry.Epoch))).Value);
        var committed = await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow);
        Assert.Equal(2, committed.Count(entry => entry.EventName == CostSheetCommittedV1.Name));
        Assert.Single(committed, entry => entry.EventName == CostCalculatedV1.Name);
        Assert.Equal(100m, (await sender.QueryAsync(new GetCostSheet(request.ItemId))).Value.UnitCost);
    }

    private static async Task WaitForTrueAsync(NpgsqlCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!Equals(true, await command.ExecuteScalarAsync(timeout.Token))) { await Task.Delay(20, timeout.Token); }
    }
}
