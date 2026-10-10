using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.Auditing.Domain.Operations;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class OperationObservationRetentionPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task Expiration_RemovesWholeOperationsAndReceipts_ReleasesQuota_AndKeepsBusinessFacts()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var old = Guid.NewGuid();
        var original = Observation(old, "started", Now.AddDays(-3));
        var recent = Guid.NewGuid();
        var cutoff = Now.AddDays(-1);
        await using (var app = Application(database.ConnectionString))
        await using (var scope = app.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
            foreach (var observation in new[]
            {
                original, Observation(old, "finished", Now.AddDays(-2)),
                Observation(recent, "started", Now.AddDays(-3)), Observation(recent, "finished", Now),
                Observation(Guid.NewGuid(), "finished", cutoff), Observation(Guid.NewGuid(), "finished", Now),
            })
            { Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(observation)).Value); }
            Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(
                new AuditFact(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
                    "global-setting", "1", 2, "actor", Now.AddYears(-1), "trace", "correlation"))).Value);
            Assert.Equal(2, await store.DeleteExpiredAsync(cutoff, 1));
            Assert.Equal(0, await store.DeleteExpiredAsync(cutoff, 1));
            var remaining = (await store.QueryAsync(new OperationQuery(1, 10))).Operations;
            Assert.Equal(3, remaining.Count);
            Assert.DoesNotContain(remaining, item => item.OperationId == old);
            Assert.NotNull(Assert.Single(remaining, item => item.OperationId == recent).StartedAt);
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(new AuditQuery(1, 10)
            { From = Now.AddYears(-1).AddDays(-1), To = Now.AddYears(-1).AddDays(1) })).Total);
            var capacity = await scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync();
            Assert.Equal(1, capacity.Facts.Records);
            Assert.Equal(4, capacity.Observations.Records);
        }
        await using var rebuilt = Application(database.ConnectionString);
        await using var retry = rebuilt.CreateAsyncScope();
        var recovered = retry.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var replay = OperationObservation.Record(original.Id, original.Data, Now).Value;
        Assert.Equal(IngestionOutcome.Accepted, (await recovered.AcceptAsync(replay)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await recovered.AcceptAsync(replay)).Value);
        Assert.Equal(0, await recovered.DeleteExpiredAsync(cutoff, 10));
        Assert.Equal(5, (await retry.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
    }

    private static ServiceProvider Application(string connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddAuditingPostgresStorage(connection);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [PostgresFact]
    public async Task RejectedCleanupCommit_KeepsObservationsReceiptsAndQuota_ThenRetryCommitsTogether()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString);
        await using var scope = app.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var original = Observation(Guid.NewGuid(), "finished", Now.AddDays(-3));
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(original)).Value);
        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            CREATE FUNCTION auditing.reject_retention_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected cleanup commit rejection'; END $$;
            CREATE CONSTRAINT TRIGGER reject_retention_commit AFTER DELETE ON auditing.inbox
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION auditing.reject_retention_commit();
            """, fault)) { await install.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAnyAsync<DbException>(() => store.DeleteExpiredAsync(Now.AddDays(-1), 10));
        Assert.Single((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
        await using (var repair = new NpgsqlCommand("""
            DROP TRIGGER reject_retention_commit ON auditing.inbox;
            DROP FUNCTION auditing.reject_retention_commit();
            """, fault)) { await repair.ExecuteNonQueryAsync(); }
        Assert.Equal(1, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(OperationObservation.Record(original.Id, original.Data, Now).Value)).Value);
    }

    [PostgresFact]
    public async Task CancelledCleanup_RollsBackDeletedRowsAndCapacity_AndRemainsRetryable()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString);
        await using var scope = app.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var original = Observation(Guid.NewGuid(), "finished", Now.AddDays(-3));
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(original)).Value);
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM auditing.central_storage_capacity WHERE \"Pool\" = 'observations' FOR UPDATE", blocker, transaction))
        { await hold.ExecuteScalarAsync(); }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteExpiredAsync(Now.AddDays(-1), 10, cancellation.Token));
        await transaction.RollbackAsync();
        Assert.Single((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
        Assert.Equal(1, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
    }

    [PostgresFact]
    public async Task ActiveAdmission_IsSkipped_AndConcurrentCleanersReleaseEachObservationOnce()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString);
        await using var scope = app.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        var operation = Guid.NewGuid();
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(Observation(operation, "started", Now.AddDays(-3)))).Value);
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", blocker, transaction))
        {
            hold.Parameters.AddWithValue("key", "auditing.operation/platform/" + operation);
            await hold.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
        await transaction.CommitAsync();
        Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(Observation(operation, "finished", Now))).Value);
        Assert.Equal(0, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
        var retained = Assert.Single((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        Assert.NotNull(retained.StartedAt);
        Assert.NotNull(retained.FinishedAt);
        var removed = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var cleaner = app.CreateAsyncScope();
            return await cleaner.ServiceProvider.GetRequiredService<IOperationObservationStore>().DeleteExpiredAsync(Now.AddDays(1), 1);
        }));
        Assert.Equal(2, removed.Sum());
        Assert.Empty((await store.QueryAsync(new OperationQuery(1, 10))).Operations);
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
    }

    [PostgresFact]
    public async Task IncrementalUpgrade_PreservesExistingObservationAndDeduplication_ThenEnablesRetention()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var original = Observation(Guid.NewGuid(), "finished", Now.AddDays(-3));
        await using (var app = Application(database.ConnectionString))
        await using (var scope = app.CreateAsyncScope())
        { Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<IOperationObservationStore>().AcceptAsync(original)).Value); }
        await using var context = new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNpgsql(database.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "auditing")).Options);
        await JourneyDatabaseOperation.RunAsync(() => context.GetService<IMigrator>().MigrateAsync("20261010010500_CentralAuditCapacity"));
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var upgraded = Application(database.ConnectionString);
        await using var reader = upgraded.CreateAsyncScope();
        var store = reader.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
        Assert.Equal(1, (await reader.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
        Assert.Equal(1, await store.DeleteExpiredAsync(Now.AddDays(-1), 10));
    }

    private static OperationObservation Observation(Guid operation, string phase, DateTimeOffset recordedAt) =>
        OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
            new OperationObservationData(new OperationId(operation), "platform", "http", phase,
                phase == "finished" ? "completed" : null, Now.AddDays(-3), "actor", "trace", "GET", "/api/example",
                phase == "finished" ? 200 : null, phase == "finished" ? 1 : null), recordedAt).Value;
}
