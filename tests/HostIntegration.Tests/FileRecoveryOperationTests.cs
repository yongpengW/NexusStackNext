using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class FileRecoveryOperationTests
{
    [PostgresFact]
    public async Task RecoveryCommitFailure_RecordsFailedAttempt_WithoutInventingACompletedFact()
    {
        await using var database = await IdentityJourneyDatabase.CreateAsync();
        await database.MigrateAsync();
        await using var provider = FilesCommittedAuditTests.BuildStorage(database.ConnectionString);
        await using var persistence = provider.CreateAsyncScope();
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var observationScope = app.Services.CreateAsyncScope();
        var root = Path.Combine(Path.GetTempPath(), "nsn-file-commit-observation-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new LocalDiskFileStore(root);
            store.EnsureCreated();
            var files = persistence.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var file = StoredFile.Register(new StoredFileId(76500), FileName.Create("unpublished.bin").Value,
                "application/octet-stream", "private-owner", clock.UtcNow).Value;
            await files.SaveAsync(file);
            file.Delete();
            await files.SaveAsync(file, 1);
            var recovery = new FileRecovery(store, files, clock, new FileRecoveryOptions(), store,
                observationScope.ServiceProvider.GetRequiredService<IBackgroundExecutionObservation>());
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var inject = new NpgsqlCommand("""
                CREATE FUNCTION files.reject_removal_fact() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Payload"::jsonb->>'operation' = 'bytes-removed' THEN
                        RAISE EXCEPTION 'Injected removal fact failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reject_removal_fact BEFORE INSERT ON files.outbox
                FOR EACH ROW EXECUTE FUNCTION files.reject_removal_fact();
                """, connection)) { await inject.ExecuteNonQueryAsync(); }
            await Assert.ThrowsAsync<DbUpdateException>(() => recovery.RunOnceAsync());
            Assert.Null((await files.FindDeletedAsync(file.Id))!.BytesRemovedAt);
            var outbox = persistence.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
            Assert.Equal(2, (await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow)).Count);
            var journal = observationScope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "recovery").ToArray();
            Assert.Equal(2, phases.Length);
            var failed = Assert.Single(phases, item => item.Phase == "finished");
            Assert.Equal("failed", failed.Outcome);
            await using (var restore = new NpgsqlCommand("DROP TRIGGER reject_removal_fact ON files.outbox", connection))
            { await restore.ExecuteNonQueryAsync(); }
            await recovery.RunOnceAsync();
            Assert.NotNull((await files.FindDeletedAsync(file.Id))!.BytesRemovedAt);
            Assert.Equal(3, (await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow)).Count);
            phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "recovery").ToArray();
            Assert.Equal(4, phases.Length);
            var completed = Assert.Single(phases, item => item.Outcome == "completed");
            Assert.NotEqual(failed.OperationId, completed.OperationId);
            Assert.All(phases, item => Assert.Null(item.ActorId));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact]
    public async Task StorageFailure_IsDeferred_AndANewAttemptCompletesWithoutInventingAnUnknownOrigin()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var root = Path.Combine(Path.GetTempPath(), "nsn-file-observation-" + Guid.NewGuid().ToString("N"));
        var unavailable = root + "-offline";
        try
        {
            using var store = new LocalDiskFileStore(root);
            store.EnsureCreated();
            var files = new InMemoryStoredFileRepository();
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            var file = StoredFile.Register(new StoredFileId(76400), FileName.Create("private-recovery.bin").Value,
                "application/octet-stream", "private-owner", clock.UtcNow).Value;
            using var content = new MemoryStream([1, 2, 3]);
            await using (var write = await store.WriteAsync(content, file.ContentType))
            {
                file.MarkStored(write.StorageKey, 3);
                await files.SaveAsync(file);
            }
            file.Delete();
            await files.SaveAsync(file, 2);
            var recovery = new FileRecovery(store, files, clock, new FileRecoveryOptions(), store,
                scope.ServiceProvider.GetRequiredService<IBackgroundExecutionObservation>());
            Directory.Move(root, unavailable);
            await File.WriteAllTextAsync(root, "storage unavailable");
            // 文件尝试先留下延期证据；随后同一挂载的孤儿扫描仍按原协议抛出 IO 故障。
            await Assert.ThrowsAnyAsync<IOException>(() => recovery.RunOnceAsync());
            var journal = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(OperationJournalServiceCollectionExtensions.OutboxKey);
            var phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "recovery").ToArray();
            Assert.Equal(2, phases.Length);
            var deferred = Assert.Single(phases, item => item.Phase == "finished");
            Assert.Equal("deferred", deferred.Outcome);
            Assert.Null((await files.FindDeletedAsync(file.Id))!.BytesRemovedAt);
            await Assert.ThrowsAnyAsync<IOException>(() => recovery.RunOnceAsync());
            Assert.Equal(2, (await OperationEndpointInventoryTests.ReadAsync(journal)).Count(item => item.Kind == "recovery"));

            File.Delete(root);
            Directory.Move(unavailable, root);
            clock.Advance(TimeSpan.FromMinutes(1));
            await recovery.RunOnceAsync();
            phases = (await OperationEndpointInventoryTests.ReadAsync(journal)).Where(item => item.Kind == "recovery").ToArray();
            Assert.Equal(4, phases.Length);
            var completed = Assert.Single(phases, item => item.Outcome == "completed");
            Assert.NotEqual(deferred.OperationId, completed.OperationId);
            Assert.NotNull((await files.FindDeletedAsync(file.Id))!.BytesRemovedAt);
            Assert.All(phases, observation =>
            {
                Assert.Null(observation.ActorId);
                Assert.Null(observation.Metadata!.InitiatorId);
                Assert.Null(observation.Metadata.ParentOperationId);
                Assert.Equal(observation.OperationId, observation.Metadata.RootOperationId);
                Assert.Equal("stored-file", observation.Metadata.SubjectType);
                Assert.Equal("76400", observation.Metadata.SubjectId);
            });
        }
        finally
        {
            if (File.Exists(root)) { File.Delete(root); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            if (Directory.Exists(unavailable)) { Directory.Delete(unavailable, recursive: true); }
        }
    }
}
