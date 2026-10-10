using System.Data.Common;
using System.Diagnostics;
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

public sealed class CentralAuditCapacityPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task FullFactPool_RollsBackRejectedIdentity_AndRecoversAfterReconfiguration()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var original = Fact();
        var rejected = Fact();
        await using (var app = Application(database.ConnectionString, 1))
        await using (var scope = app.CreateAsyncScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<AuditIngestion>();
            Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(original)).Value);
            Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(original)).Value);
            Assert.Equal(AuditIngestion.MessageConflict, (await ingestion.IngestAsync(original with { SubjectId = "2" })).Error);
            Assert.True((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => ingestion.IngestAsync(rejected))).CapacityExhausted);
            Assert.Equal(original, Assert.Single((await scope.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10)).Entries).Fact);
            var observation = OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
                new OperationObservationData(new OperationId(Guid.NewGuid()), "platform", "http", "finished", "completed", Now,
                    "actor", "trace", "GET", "/api/example", 200, 1), Now).Value;
            Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<IOperationObservationStore>().AcceptAsync(observation)).Value);
        }
        await using var recovered = Application(database.ConnectionString, 2);
        await using var retry = recovered.CreateAsyncScope();
        var resumed = retry.ServiceProvider.GetRequiredService<AuditIngestion>();
        Assert.Equal(IngestionOutcome.Duplicate, (await resumed.IngestAsync(original)).Value);
        Assert.Equal(IngestionOutcome.Accepted, (await resumed.IngestAsync(rejected)).Value);
        Assert.Equal(2, (await retry.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10)).Total);
        var capacity = await retry.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync();
        Assert.Equal(new AuditStoragePoolCapacity(2, 2), capacity.Facts);
        Assert.Equal(new AuditStoragePoolCapacity(1, 1), capacity.Observations);
    }

    [PostgresFact]
    public async Task ConcurrentWriters_CannotOverspendTheLastFactSlot()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString, 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = app.CreateAsyncScope();
            try { return (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(Fact())).Value == IngestionOutcome.Accepted; }
            catch (AuditStorageUnavailableException failure) when (failure.CapacityExhausted) { return false; }
        }));
        Assert.Single(results, accepted => accepted);
        await using var reader = app.CreateAsyncScope();
        Assert.Equal(1, (await reader.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Facts.Records);
        Assert.Equal(1, (await reader.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10)).Total);
    }

    [PostgresFact]
    public async Task DeferredCommitFailure_RollsBackQuotaAndInbox_ThenOriginalMessageIsAcceptedOnce()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString, 1);
        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            CREATE FUNCTION auditing.reject_capacity_test_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected commit rejection'; END $$;
            CREATE CONSTRAINT TRIGGER reject_capacity_test_commit AFTER INSERT ON auditing.audit_entries
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION auditing.reject_capacity_test_commit();
            """, fault)) { await install.ExecuteNonQueryAsync(); }
        var message = Fact();
        await using (var writer = app.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<DbException>(() => writer.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(message));
        }
        await using (var reader = app.CreateAsyncScope())
        {
            Assert.Equal(0, (await reader.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Facts.Records);
            Assert.Empty((await reader.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10)).Entries);
        }
        await using (var repair = new NpgsqlCommand("DROP TRIGGER reject_capacity_test_commit ON auditing.audit_entries", fault)) { await repair.ExecuteNonQueryAsync(); }
        await using var retry = app.CreateAsyncScope();
        var ingestion = retry.ServiceProvider.GetRequiredService<AuditIngestion>();
        Assert.Equal(IngestionOutcome.Accepted, (await ingestion.IngestAsync(message)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(message)).Value);
    }

    [PostgresFact]
    public async Task LedgerContention_HasFiniteWaitAndCallerCancellation_WithoutConsumingCapacity()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString, 1, waitMilliseconds: 500);
        await using var blocking = new NpgsqlConnection(database.ConnectionString);
        await blocking.OpenAsync();
        await using var transaction = await blocking.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT * FROM auditing.central_storage_capacity WHERE \"Pool\" = 'facts' FOR UPDATE", blocking, transaction))
        { await hold.ExecuteNonQueryAsync(); }
        var message = Fact();
        var elapsed = Stopwatch.StartNew();
        await using (var writer = app.CreateAsyncScope())
        {
            Assert.False((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() =>
                writer.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(message))).CapacityExhausted);
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await using (var writer = app.CreateAsyncScope())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(message, cancellation.Token));
        }
        await transaction.RollbackAsync();
        await using var retry = app.CreateAsyncScope();
        Assert.Equal(0, (await retry.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Facts.Records);
        Assert.Equal(IngestionOutcome.Accepted, (await retry.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(message)).Value);
    }

    [PostgresFact]
    public async Task Upgrade_BackfillsExistingRecordsWithoutChangingFingerprintsOrRemovingHistory()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var message = Fact();
        await using (var original = Application(database.ConnectionString, 2))
        await using (var scope = original.CreateAsyncScope())
        { Assert.Equal(IngestionOutcome.Accepted, (await scope.ServiceProvider.GetRequiredService<AuditIngestion>().IngestAsync(message)).Value); }
        await using var context = new AuditingDbContext(new DbContextOptionsBuilder<AuditingDbContext>()
            .UseNpgsql(database.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "auditing")).Options);
        var migrator = context.GetService<IMigrator>();
        await JourneyDatabaseOperation.RunAsync(() => migrator.MigrateAsync("20261004171835_CapacityPolicyAuditEvidence"));
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var upgraded = Application(database.ConnectionString, 1);
        await using var reader = upgraded.CreateAsyncScope();
        Assert.Equal(1, (await reader.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Facts.Records);
        var ingestion = reader.ServiceProvider.GetRequiredService<AuditIngestion>();
        Assert.Equal(IngestionOutcome.Duplicate, (await ingestion.IngestAsync(message)).Value);
        Assert.True((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => ingestion.IngestAsync(Fact()))).CapacityExhausted);
        Assert.Equal(message, Assert.Single((await reader.ServiceProvider.GetRequiredService<IAuditEntryStore>().QueryAsync(1, 10)).Entries).Fact);
    }

    private static AuditFact Fact() => new(Guid.NewGuid(), "platform.setting-committed.v1", "platform", "platform.setting.changed",
        "global-setting", "1", 2, "actor", Now, "trace", "correlation");

    [PostgresFact]
    public async Task FullObservationPool_PreservesDuplicatesAndPhaseConflicts_ThenRetriesWithoutAReservedPhase()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        var original = Observation();
        var rejected = Observation();
        await using (var app = Application(database.ConnectionString, 1))
        await using (var scope = app.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOperationObservationStore>();
            Assert.Equal(IngestionOutcome.Accepted, (await store.AcceptAsync(original)).Value);
            Assert.Equal(IngestionOutcome.Duplicate, (await store.AcceptAsync(original)).Value);
            Assert.Equal(OperationObservationIngestion.MessageConflict, (await store.AcceptAsync(
                OperationObservation.Record(original.Id, original.Data with { DurationMs = 2 }, Now).Value)).Error);
            Assert.Equal(OperationObservationIngestion.PhaseConflict, (await store.AcceptAsync(
                OperationObservation.Record(new OperationObservationId(Guid.NewGuid()), original.Data, Now).Value)).Error);
            Assert.True((await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => store.AcceptAsync(rejected))).CapacityExhausted);
        }
        await using var restored = Application(database.ConnectionString, 1, maxObservations: 2);
        await using var retry = restored.CreateAsyncScope();
        var recovered = retry.ServiceProvider.GetRequiredService<IOperationObservationStore>();
        Assert.Equal(IngestionOutcome.Accepted, (await recovered.AcceptAsync(rejected)).Value);
        Assert.Equal(IngestionOutcome.Duplicate, (await recovered.AcceptAsync(rejected)).Value);
        Assert.Equal(2, (await recovered.QueryAsync(new OperationQuery(1, 10))).Total);
        Assert.Equal(new AuditStoragePoolCapacity(2, 2), (await retry.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations);
    }

    [PostgresFact]
    public async Task MissingLedgerPool_IsUnavailableInsteadOfAnApparentlyEmptyStore()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        Assert.Equal(0, (await IdentityJourneyDatabase.RunMigrationAsync(database.ConnectionString, "Auditing")).ExitCode);
        await using var app = Application(database.ConnectionString, 1);
        await using var scope = app.CreateAsyncScope();
        await using var fault = new NpgsqlConnection(database.ConnectionString);
        await fault.OpenAsync();
        await using (var remove = new NpgsqlCommand("DELETE FROM auditing.central_storage_capacity WHERE \"Pool\" = 'observations'", fault))
        { await remove.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<AuditStorageUnavailableException>(() => scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync());
        await using (var repair = new NpgsqlCommand("INSERT INTO auditing.central_storage_capacity VALUES ('observations', 0)", fault))
        { await repair.ExecuteNonQueryAsync(); }
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<IAuditStorageCapacityReader>().ReadAsync()).Observations.Records);
    }

    private static OperationObservation Observation() => OperationObservation.Record(new OperationObservationId(Guid.NewGuid()),
        new OperationObservationData(new OperationId(Guid.NewGuid()), "platform", "http", "finished", "completed", Now,
            "actor", "trace", "GET", "/api/example", 200, 1), Now).Value;

    private static ServiceProvider Application(string connection, long maxFacts, int waitMilliseconds = 3000, long maxObservations = 1)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator(1000));
        services.AddAuditingPostgresStorage(connection, new() { MaxFacts = maxFacts, MaxObservations = maxObservations, WaitTimeoutMilliseconds = waitMilliseconds });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
