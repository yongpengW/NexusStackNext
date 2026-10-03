using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Pricing.Infrastructure.Migrations;
using NexusStackNext.PricingHost;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingTaskUpgradeTests
{
    [PostgresFact]
    public async Task MissingTaskMetadataColumn_FailsReadinessAndOrdinaryStartup()
    {
        var database = new PricingDatabaseFixture();
        await database.InitializeAsync();
        try
        {
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var damage = new NpgsqlCommand("ALTER TABLE pricing.tasks DROP COLUMN \"MaxLeaseUntil\"", connection);
                await damage.ExecuteNonQueryAsync();
            }
            Assert.False(await PricingDatabase.IsReadyAsync(database.ConnectionString));
            var startup = await BusinessProcess.RunToExitAsync(BusinessProcess.StartInfo(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString));
            Assert.Equal(1, startup.ExitCode);
        }
        finally { await database.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Upgrade_PreservesUnknownLegacyTimes_AndAllowsOriginalLeaseCompletion()
    {
        var database = new PricingDatabaseFixture { MigrateOnInitialize = false };
        await database.InitializeAsync();
        try
        {
            await LegacyMigrations.ApplyAsync(database.ConnectionString, "pricing", new InitialPricing(), new TaskExecutionOrigin(), new OutboxRetryRevision(), new CommittedFactCleanup());
            var pending = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var running = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var failed = Guid.Parse("00000000-0000-0000-0000-000000000003");
            var expired = Guid.Parse("00000000-0000-0000-0000-000000000004");
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO pricing.quotes ("Id", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "Version", "CreatedAt")
                    SELECT id, 80, 0.2, 1, 0, 1, clock_timestamp() FROM unnest(ARRAY[@pending,@running,@failed,@expired]::uuid[]) AS id;
                    INSERT INTO pricing.tasks ("TaskId", "ItemId", "ExpectedVersion", "Cost", "FeeRate", "InputRevision", "State", "Epoch", "Attempts", "LeaseUntil")
                    VALUES (@pending,@pending,0,80,0.2,1,'Pending',0,0,null),
                           (@running,@running,0,80,0.2,1,'Running',1,1,clock_timestamp() + interval '1 minute'),
                           (@failed,@failed,0,80,0.2,1,'Failed',1,1,clock_timestamp() - interval '1 minute'),
                           (@expired,@expired,0,80,0.2,1,'Running',1,1,clock_timestamp() - interval '1 minute');
                    INSERT INTO pricing.attempts ("TaskId", "Epoch", "StartedAt", "Outcome", "FinishedAt")
                    VALUES (@running,1,clock_timestamp(),'Running',null), (@failed,1,clock_timestamp() - interval '1 minute','Failed',clock_timestamp()),
                           (@expired,1,clock_timestamp() - interval '2 minutes','Running',null);
                    """, connection);
                seed.Parameters.AddWithValue("pending", pending);
                seed.Parameters.AddWithValue("running", running);
                seed.Parameters.AddWithValue("failed", failed);
                seed.Parameters.AddWithValue("expired", expired);
                await seed.ExecuteNonQueryAsync();
            }
            Assert.False(await PricingDatabase.IsReadyAsync(database.ConnectionString));
            await PricingDatabase.MigrateAsync(database.ConnectionString);
            await PricingDatabase.MigrateAsync(database.ConnectionString);
            Assert.True(await PricingDatabase.IsReadyAsync(database.ConnectionString));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNexusStackApplication();
            services.AddPricingPostgres(database.ConnectionString);
            await using var application = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var scope = application.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            foreach (var id in new[] { pending, running, failed, expired })
            {
                var status = (await sender.QueryAsync(new GetRecalculation(id))).Value;
                Assert.Null(status.CreatedAt);
                Assert.Null(status.MaxLeaseUntil);
            }
            var legacyPage = (await sender.QueryAsync(new ListRecalculations())).Value;
            Assert.Equal(new[] { pending, running, failed, expired }, legacyPage.Items.Select(task => task.TaskId));
            var replay = new UpdatePricingCost(pending, pending, 0, 80m, 0.2m);
            Assert.Null((await sender.SendAsync(replay)).Value.CreatedAt);
            Assert.Equal("pricing.request_conflict", (await sender.SendAsync(replay with { DelaySeconds = 1 })).Error.Code);
            Assert.Equal("pricing.renew_conflict", (await sender.SendAsync(new RenewPricingWork(running, 1))).Error.Code);
            Assert.True((await sender.SendAsync(new CompletePricingWork(running, 1))).Value);
            Assert.Equal(100m, (await sender.QueryAsync(new GetPriceQuote(running))).Value.BreakEvenPrice);
            _ = (await sender.SendAsync(new CancelPricingWork(pending, 0))).Value;
            var takeover = (await sender.SendAsync(new ClaimPricingWork())).Value!;
            Assert.Equal(expired, takeover.TaskId);
            Assert.Equal(2, takeover.Epoch);
            var takenOver = (await sender.QueryAsync(new GetRecalculation(expired))).Value;
            Assert.Null(takenOver.CreatedAt);
            Assert.NotNull(takenOver.MaxLeaseUntil);
            Assert.Equal("pricing.renew_conflict", (await sender.SendAsync(new RenewPricingWork(expired, 1))).Error.Code);
            Assert.True((await sender.SendAsync(new CompletePricingWork(expired, takeover.Epoch))).Value);
            var current = (await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), Guid.NewGuid(), 0, 90m, 0.1m, DelaySeconds: 120))).Value;
            Assert.NotNull(current.CreatedAt);
            Assert.Equal(current.TaskId, (await sender.QueryAsync(new ListRecalculations(Limit: 1))).Value.Items[0].TaskId);
            var reopened = (await sender.SendAsync(new RetryPricingWork(failed, 1))).Value;
            Assert.Null(reopened.CreatedAt);
            Assert.Equal("Retry", reopened.State);
        }
        finally { await database.DisposeAsync(); }
    }
}
