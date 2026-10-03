using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Costing.Application;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Costing.Infrastructure.Migrations;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingTaskUpgradeTests
{
    [PostgresFact]
    public async Task MissingTaskMetadataColumn_FailsReadinessAndOrdinaryStartup()
    {
        var database = new CostingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var damage = new NpgsqlCommand("ALTER TABLE costing.tasks DROP COLUMN \"MaxLeaseUntil\"", connection);
                await damage.ExecuteNonQueryAsync();
            }
            Assert.False(await CostingDatabase.IsReadyAsync(database.ConnectionString));
            var startup = await BusinessProcess.RunToExitAsync(BusinessProcess.StartInfo(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString));
            Assert.Equal(1, startup.ExitCode);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Upgrade_PreservesUnknownLegacyTimes_AndAllowsOriginalLeaseCompletion()
    {
        var database = new CostingDatabaseFixture { MigrateOnInitialize = false };
        await database.InitializeAsync();
        try
        {
            await LegacyMigrations.ApplyAsync(database.ConnectionString, "costing", new InitialCosting(), new SharedTaskProtocol(), new ScheduledCostReceipts());
            var pending = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var running = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var failed = Guid.Parse("00000000-0000-0000-0000-000000000003");
            var expired = Guid.Parse("00000000-0000-0000-0000-000000000004");
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO costing.sheets ("Id", "PurchaseCost", "FreightCost", "InputRevision", "CalculatedRevision", "Version")
                    SELECT id, 80, 20, 1, 0, 1 FROM unnest(ARRAY[@pending,@running,@failed,@expired]::uuid[]) AS id;
                    INSERT INTO costing.tasks ("TaskId", "ItemId", "ExpectedVersion", "PurchaseCost", "FreightCost", "InputRevision", "State", "Epoch", "Attempts", "LeaseUntil")
                    VALUES (@pending,@pending,0,80,20,1,'Pending',0,0,null),
                           (@running,@running,0,80,20,1,'Running',1,1,clock_timestamp() + interval '1 minute'),
                           (@failed,@failed,0,80,20,1,'Failed',1,1,clock_timestamp() - interval '1 minute'),
                           (@expired,@expired,0,80,20,1,'Running',1,1,clock_timestamp() - interval '1 minute');
                    INSERT INTO costing.attempts ("TaskId", "Epoch", "StartedAt", "Outcome", "FinishedAt")
                    VALUES (@running,1,clock_timestamp(),'Running',null), (@failed,1,clock_timestamp() - interval '1 minute','Failed',clock_timestamp()),
                           (@expired,1,clock_timestamp() - interval '2 minutes','Running',null);
                    """, connection);
                seed.Parameters.AddWithValue("pending", pending);
                seed.Parameters.AddWithValue("running", running);
                seed.Parameters.AddWithValue("failed", failed);
                seed.Parameters.AddWithValue("expired", expired);
                await seed.ExecuteNonQueryAsync();
            }
            Assert.False(await CostingDatabase.IsReadyAsync(database.ConnectionString));
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            await CostingDatabase.MigrateAsync(database.ConnectionString);
            Assert.True(await CostingDatabase.IsReadyAsync(database.ConnectionString));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNexusStackApplication();
            services.AddCostingPostgres(database.ConnectionString);
            await using var application = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            foreach (var id in new[] { pending, running, failed, expired })
            {
                var status = (await sender.QueryAsync(new GetCostCalculation(id))).Value;
                Assert.Null(status.CreatedAt);
                Assert.Null(status.MaxLeaseUntil);
            }
            var legacyPage = (await sender.QueryAsync(new ListCostCalculations())).Value;
            Assert.Equal(new[] { pending, running, failed, expired }, legacyPage.Items.Select(task => task.TaskId));
            var replay = new UpdateCostInputs(pending, pending, 0, 80m, 20m);
            Assert.Null((await sender.SendAsync(replay)).Value.CreatedAt);
            Assert.Equal("costing.request_conflict", (await sender.SendAsync(replay with { DelaySeconds = 1 })).Error.Code);
            Assert.Equal("costing.renew_conflict", (await sender.SendAsync(new RenewCostingWork(running, 1))).Error.Code);
            Assert.True((await sender.SendAsync(new CompleteCostingWork(running, 1))).Value);
            Assert.Equal(100m, (await sender.QueryAsync(new GetCostSheet(running))).Value.UnitCost);
            _ = (await sender.SendAsync(new CancelCostingWork(pending, 0))).Value;
            var takeover = (await sender.SendAsync(new ClaimCostingWork())).Value!;
            Assert.Equal(expired, takeover.TaskId);
            Assert.Equal(2, takeover.Epoch);
            var takenOver = (await sender.QueryAsync(new GetCostCalculation(expired))).Value;
            Assert.Null(takenOver.CreatedAt);
            Assert.NotNull(takenOver.MaxLeaseUntil);
            Assert.Equal("costing.renew_conflict", (await sender.SendAsync(new RenewCostingWork(expired, 1))).Error.Code);
            Assert.True((await sender.SendAsync(new CompleteCostingWork(expired, takeover.Epoch))).Value);
            var current = (await sender.SendAsync(new UpdateCostInputs(Guid.NewGuid(), Guid.NewGuid(), 0, 90m, 10m, DelaySeconds: 120))).Value;
            Assert.NotNull(current.CreatedAt);
            Assert.Equal(current.TaskId, (await sender.QueryAsync(new ListCostCalculations(Limit: 1))).Value.Items[0].TaskId);
            var reopened = (await sender.SendAsync(new RetryCostingWork(failed, 1))).Value;
            Assert.Null(reopened.CreatedAt);
            Assert.Equal("Retry", reopened.State);
        }
        finally { await database.DisposeAsync(); }
    }
}
