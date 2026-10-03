using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Costing.Endpoints;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Infrastructure.Persistence;
using NexusStackNext.Identity.Contracts;
using NexusStackNext.Identity.Infrastructure.Persistence;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Infrastructure.Persistence;
using NexusStackNext.Pricing.Contracts;
using NexusStackNext.Pricing.Endpoints;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Infrastructure.Persistence;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CommittedFactCleanupHostTests
{
    [PostgresFact]
    public async Task BusinessModules_AutomaticallyCleanFactCopies_AndPreserveBusinessDeliveries()
    {
        foreach (var name in new[] { "Costing", "Pricing" })
        {
            await using var database = await IdentityJourneyDatabase.CreateAsync();
            if (name == "Costing") { await CostingDatabase.MigrateAsync(database.ConnectionString); }
            else { await PricingDatabase.MigrateAsync(database.ConnectionString); }
            var schema = name.ToLowerInvariant();
            var factName = name == "Costing" ? CostSheetCommittedV1.Name : PriceQuoteCommittedV1.Name;
            var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
            var factId = Guid.NewGuid();
            var businessId = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var seed = new NpgsqlCommand($$"""
                INSERT INTO {{schema}}.outbox ("Id", "EventName", "Payload", "OccurredAt", "AttemptCount", "DeliveredAt")
                VALUES (@fact, @event, '{}', @at, 0, @at), (@business, 'business.delivery.v1', '{}', @at, 0, @at)
                """, connection))
            {
                seed.Parameters.AddWithValue("fact", factId);
                seed.Parameters.AddWithValue("business", businessId);
                seed.Parameters.AddWithValue("event", factName);
                seed.Parameters.AddWithValue("at", now.AddHours(-1));
                await seed.ExecuteNonQueryAsync();
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{name}"] = database.ConnectionString,
                [$"{name}:Worker:Enabled"] = "false",
                [$"{name}:Messaging:Enabled"] = "false",
                [$"{name}:AuditDelivery:Cleanup:DeliveredRetention"] = "01:00:00",
            }).Build();
            using var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddLogging(logging => logging.ClearProviders());
                services.AddNexusStackApplication();
                services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 21 });
                services.AddSingleton<IClock>(new FixedClock(now));
                if (name == "Costing") { services.AddCostingModule(configuration); }
                else { services.AddPricingModule(configuration); }
            }).Build();
            await host.StartAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var query = new NpgsqlCommand($"SELECT \"Id\" FROM {schema}.outbox ORDER BY \"Id\"", connection);
                while (true)
                {
                    var ids = new List<Guid>();
                    await using (var reader = await query.ExecuteReaderAsync(timeout.Token))
                    {
                        while (await reader.ReadAsync(timeout.Token)) { ids.Add(reader.GetGuid(0)); }
                    }
                    if (!ids.Contains(factId)) { Assert.Equal(businessId, Assert.Single(ids)); break; }
                    await Task.Delay(50, timeout.Token);
                }
            }
            finally { await host.StopAsync(); }
        }
    }

    [PostgresFact]
    public async Task CleanupFailure_DegradesDiagnostics_KeepsCopies_AndWorkerRecovers()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await AssertWorkerAsync<PlatformDbContext>(database.ConnectionString, PlatformDbContext.SchemaName, SettingCommittedV1.Name,
            options => new PlatformDbContext(options), failFirst: true);
    }

    [PostgresFact]
    public async Task IndependentContextWorkers_CleanOnlyTheirConfirmedFactCopies_WithoutBroker()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await AssertWorkerAsync<IdentityDbContext>(database.ConnectionString, IdentityDbContext.SchemaName, IdentityEntityCommittedV1.Name, options => new IdentityDbContext(options));
        await AssertWorkerAsync<PlatformDbContext>(database.ConnectionString, PlatformDbContext.SchemaName, SettingCommittedV1.Name, options => new PlatformDbContext(options));
        await AssertWorkerAsync<FilesDbContext>(database.ConnectionString, FilesDbContext.SchemaName, StoredFileCommittedV1.Name, options => new FilesDbContext(options));
        await AssertWorkerAsync<SchedulingDbContext>(database.ConnectionString, SchedulingDbContext.SchemaName, PlanCommittedV1.Name, options => new SchedulingDbContext(options));
    }

    private static async Task AssertWorkerAsync<TContext>(string connection, string schema, string factEvent,
        Func<DbContextOptions<TContext>, TContext> factory, bool failFirst = false) where TContext : NexusStackDbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseNexusStackPostgres(connection, schema).Options;
        await using var check = factory(options);
        await check.Database.MigrateAsync();
        Assert.Equal(1, await check.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = {schema} AND indexname = 'ix_outbox_confirmed_event'").SingleAsync());
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var fact = new OutboxEntry { Id = Guid.NewGuid(), EventName = factEvent, Payload = "{}", OccurredAt = now.AddHours(-2), DeliveredAt = now.AddHours(-1) };
        var business = fact with { Id = Guid.NewGuid(), EventName = "business.delivery.v1" };
        var pending = fact with { Id = Guid.NewGuid(), DeliveredAt = null };
        check.Outbox.AddRange(fact, business, pending);
        await check.SaveChangesAsync();
        if (failFirst)
        {
            await check.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION platform.reject_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'private maintenance fault'; END $$;
                CREATE TRIGGER reject_cleanup AFTER DELETE ON platform.outbox FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_cleanup();
                """);
        }
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IClock>(new FixedClock(now));
            services.AddScoped(_ => factory(options));
            services.AddCommittedFactCleanup<TContext>(schema, factEvent, new() { DeliveredRetention = TimeSpan.FromHours(1), Interval = TimeSpan.FromSeconds(1) });
        }).Build();
        await host.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var health = host.Services.GetRequiredService<HealthCheckService>();
            if (failFirst)
            {
                HealthReport report;
                do
                {
                    report = await health.CheckHealthAsync(cancellationToken: timeout.Token);
                    if (report.Status != HealthStatus.Degraded) { await Task.Delay(50, timeout.Token); }
                } while (report.Status != HealthStatus.Degraded);
                Assert.Equal(3, await check.Outbox.CountAsync());
                Assert.DoesNotContain("private maintenance fault", report.Entries[$"{schema}-fact-cleanup"].Description!, StringComparison.Ordinal);
                await check.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cleanup ON platform.outbox");
            }
            while (await check.Outbox.AsNoTracking().AnyAsync(entry => entry.Id == fact.Id))
            {
                await Task.Delay(50, timeout.Token);
            }
            Assert.Equal(new[] { business.Id, pending.Id }.Order(), (await check.Outbox.AsNoTracking().Select(entry => entry.Id).ToArrayAsync()).Order());
            while ((long)(await health.CheckHealthAsync(cancellationToken: timeout.Token)).Entries[$"{schema}-fact-cleanup"].Data["deletedCopies"] != 1)
            {
                await Task.Delay(50, timeout.Token);
            }
            var final = (await health.CheckHealthAsync()).Entries[$"{schema}-fact-cleanup"];
            Assert.Equal(failFirst, (long)final.Data["cleanupFailures"] > 0);
            Assert.False((bool)final.Data["cleanupDegraded"]);
            Assert.True((long)final.Data["cleanupRuns"] > 0);
            Assert.Equal(1L, final.Data["deletedCopies"]);
        }
        finally { await host.StopAsync(); }
    }
}
