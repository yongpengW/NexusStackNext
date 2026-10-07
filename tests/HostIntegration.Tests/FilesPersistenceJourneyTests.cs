using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FilesPersistenceJourneyTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task DefaultProcessStorage_IsIsolatedAndOnlyOwnedBytesAreCleaned()
    {
        await using var firstDatabase = await databases.CreateAsync();
        await using var secondDatabase = await databases.CreateAsync();
        string firstRoot;
        string secondRoot;
        await using (var second = await PlatformHostProcess.StartAsync(secondDatabase.ConnectionString, "files-root-password"))
        {
            secondRoot = second.FilesRoot;
            await using (var first = await PlatformHostProcess.StartAsync(firstDatabase.ConnectionString, "files-root-password"))
            {
                firstRoot = first.FilesRoot;
                Assert.NotEqual(firstRoot, secondRoot);
                await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "files-root-password");
                await PlatformSettingsAccessTests.LoginAsync(second.Client, "journey-root", "files-root-password");
                using var firstBytes = new ByteArrayContent([1, 2]);
                using var secondBytes = new ByteArrayContent([3, 4]);
                using var firstUpload = await first.Client.PostAsync(new Uri("/api/files?name=first.bin", UriKind.Relative), firstBytes);
                using var secondUpload = await second.Client.PostAsync(new Uri("/api/files?name=second.bin", UriKind.Relative), secondBytes);
                Assert.Equal(HttpStatusCode.Created, firstUpload.StatusCode);
                Assert.Equal(HttpStatusCode.Created, secondUpload.StatusCode);
                Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Assert.Single(Directory.EnumerateFiles(firstRoot, "v1-*"), path => Path.GetExtension(path).Length == 0)));
                Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Assert.Single(Directory.EnumerateFiles(secondRoot, "v1-*"), path => Path.GetExtension(path).Length == 0)));
            }
            Assert.False(Directory.Exists(firstRoot));
            Assert.True(Directory.Exists(secondRoot));
            using var health = await second.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        Assert.False(Directory.Exists(secondRoot));
    }

    [PostgresFact]
    public async Task FileAudit_SoftDeletionPreservesCreationAndRecordsTheDeletingActor()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-root-password");
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var upload = await client.PostAsync(new Uri("/api/files?name=audit-delete.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        using var metadata = await client.GetAsync(new Uri($"/api/files/{id}/metadata", UriKind.Relative));
        var audit = (await metadata.Content.ReadApiDataAsync()).GetProperty("audit");
        var beforeDelete = DateTimeOffset.UtcNow;
        using var deleted = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var reopened = app.Services.CreateAsyncScope();
        var file = await reopened.ServiceProvider.GetRequiredService<IStoredFileRepository>().FindDeletedAsync(new StoredFileId(id));
        Assert.NotNull(file);
        Assert.True(file.IsDeleted);
        Assert.Equal(audit.GetProperty("createdAt").GetDateTimeOffset(), file.CreatedAt);
        Assert.Equal(audit.GetProperty("createdBy").GetString(), file.CreatedBy);
        Assert.Equal(file.CreatedBy, file.UpdatedBy);
        Assert.NotNull(file.UpdatedAt);
        Assert.InRange(file.UpdatedAt.Value, beforeDelete, DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task FirstCleanupAttempt_IsNotStarvedByDueFailingRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-pending-fairness-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            long firstId;
            long nextId;
            string firstBytes;
            await using (var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root))
            {
                await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "files-root-password");
                using var firstContent = new ByteArrayContent([1]);
                using var first = await host.Client.PostAsync(new Uri("/api/files?name=first.bin", UriKind.Relative), firstContent);
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);
                firstId = (await first.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                firstBytes = Assert.Single(Directory.EnumerateFiles(root, "v1-*"), path => Path.GetExtension(path).Length == 0);
                using var nextContent = new ByteArrayContent([2]);
                using var next = await host.Client.PostAsync(new Uri("/api/files?name=next.bin", UriKind.Relative), nextContent);
                Assert.Equal(HttpStatusCode.Created, next.StatusCode);
                nextId = (await next.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            }
            // 构造删除途中退出后的两个持久状态：一个清理始终失败，一个尚未开始首次清理。
            File.Move(firstBytes, Path.Combine(root, "retained-original"));
            Directory.CreateDirectory(firstBytes);
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var interrupted = new NpgsqlCommand("""
                    UPDATE files.stored_files SET "IsDeleted" = true, "Version" = "Version" + 1,
                        "NextCleanupAttemptAt" = CASE WHEN "Id" = @first THEN NOW() - INTERVAL '1 minute' ELSE NULL END
                    """, connection);
                interrupted.Parameters.AddWithValue("first", firstId);
                Assert.Equal(2, await interrupted.ExecuteNonQueryAsync());
            }
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root, cleanupBatchSize: 1);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "files-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var status = await restarted.Client.GetAsync(new Uri($"/api/files/{nextId}/deletion", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                if ((await status.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean()) { break; }
                await Task.Delay(50, timeout.Token);
            }
            using var failed = await restarted.Client.GetAsync(new Uri($"/api/files/{firstId}/deletion", UriKind.Relative));
            Assert.False((await failed.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean());
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [PostgresFact]
    public Task UnremovableOrphan_DoesNotBlockOtherOrphanRecovery() => AssertOrphanRecoveryContinuesAsync(false);

    [PostgresFact]
    public Task UnwritableOrphanProtection_DoesNotBlockOtherOrphanRecovery() => AssertOrphanRecoveryContinuesAsync(true);

    private async Task AssertOrphanRecoveryContinuesAsync(bool unwritableProtection)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-orphan-fairness-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var reject = new NpgsqlCommand("ALTER TABLE files.stored_files ADD CONSTRAINT reject_test_file CHECK (\"Name\" <> 'reject.bin')", connection);
                await reject.ExecuteNonQueryAsync();
            }
            await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "files-root-password");
            using var firstContent = new ByteArrayContent([1]);
            using var first = await host.Client.PostAsync(new Uri("/api/files?name=reject.bin", UriKind.Relative), firstContent);
            Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
            var original = Assert.Single(Directory.EnumerateFiles(root, "v1-*"), path => Path.GetExtension(path).Length == 0);
            // 用排序靠前的受管句柄制造永久 I/O 故障，原字节留在测试目录供最终清理。
            var blocked = original[..^32] + new string('0', 32);
            File.Move(original, unwritableProtection ? blocked : Path.Combine(root, "retained-original"));
            File.Move(original + ".lock", blocked + ".lock");
            if (unwritableProtection) { File.SetAttributes(blocked + ".lock", FileAttributes.ReadOnly); }
            else { Directory.CreateDirectory(blocked); }
            using var nextContent = new ByteArrayContent([2]);
            using var next = await host.Client.PostAsync(new Uri("/api/files?name=reject.bin", UriKind.Relative), nextContent);
            Assert.Equal(HttpStatusCode.InternalServerError, next.StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Directory.EnumerateFiles(root, "v1-*").Any(path => path != blocked + ".lock" && path != blocked))
            { await Task.Delay(50, timeout.Token); }
            Assert.True(unwritableProtection ? File.Exists(blocked) : Directory.Exists(blocked));
            Assert.True(File.Exists(blocked + ".lock"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.lock")) { File.SetAttributes(path, FileAttributes.Normal); }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [PostgresFact]
    public async Task ConcurrentDeletes_BothObserveDurableDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-concurrent-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            using var client = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-root-password");
            using var content = new ByteArrayContent([1, 2]);
            using var uploaded = await client.PostAsync(new Uri("/api/files?name=concurrent.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            await using (var setup = new NpgsqlCommand("""
                CREATE FUNCTION files.hold_test_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT OLD."IsDeleted" AND NEW."IsDeleted" THEN
                        PERFORM pg_advisory_xact_lock(360036, 2);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER hold_test_delete BEFORE UPDATE ON files.stored_files
                    FOR EACH ROW EXECUTE FUNCTION files.hold_test_delete();
                """, observer)) { await setup.ExecuteNonQueryAsync(); }
            await using var blocker = new NpgsqlConnection(database.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(360036, 2)", blocker, transaction))
            { await hold.ExecuteNonQueryAsync(); }
            var first = client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            await WaitForDatabaseSignalAsync(observer, """
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND query LIKE 'UPDATE files.stored_files%' AND wait_event = 'advisory')
                """);
            var second = client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            try
            {
                await WaitForDatabaseSignalAsync(observer, """
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                        AND query LIKE '%hashtextextended%' AND wait_event = 'advisory')
                    """);
            }
            finally { await transaction.RollbackAsync(); }
            using var firstResult = await first;
            using var secondResult = await second;
            Assert.Contains(firstResult.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Accepted });
            Assert.Contains(secondResult.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Accepted });
            using var repeated = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
            using var hidden = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [PostgresFact]
    public async Task Recovery_PreservesLiveUpload_AndRemovesInterruptedUploadAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-interrupted-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var reject = new NpgsqlCommand("ALTER TABLE files.stored_files ADD CONSTRAINT reject_test_file CHECK (\"Name\" <> 'reject.bin')", connection);
                await reject.ExecuteNonQueryAsync();
            }
            await using var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            using var client = new HttpClient { BaseAddress = first.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-root-password");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var liveContent = new PausedUploadContent(cancellation.Token);
            var liveUpload = client.PostAsync(new Uri("/api/files?name=slow.bin", UriKind.Relative), liveContent, cancellation.Token);
            while (!Directory.EnumerateFiles(root, "*.part").Any()) { await Task.Delay(20, cancellation.Token); }
            using var rejectedContent = new ByteArrayContent([9]);
            using var rejected = await client.PostAsync(new Uri("/api/files?name=reject.bin", UriKind.Relative), rejectedContent);
            Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
            // 回收掉稍后产生的失败上传，证明恢复者已经实际运行，不能仅凭等待时间声称未误删。
            while (Directory.EnumerateFiles(root, "v1-*").Count() != 2) { await Task.Delay(50, cancellation.Token); }
            liveContent.Complete();
            using var uploaded = await liveUpload;
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
            using var initialDownload = await client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, initialDownload.StatusCode);
            Assert.Equal(new byte[] { 1, 2 }, await initialDownload.Content.ReadAsByteArrayAsync());

            using var interruptedContent = new PausedUploadContent(cancellation.Token);
            var interrupted = client.PostAsync(new Uri("/api/files?name=interrupted.bin", UriKind.Relative), interruptedContent, cancellation.Token);
            while (!Directory.EnumerateFiles(root, "*.part").Any()) { await Task.Delay(20, cancellation.Token); }
            await first.CrashAsync();
            await cancellation.CancelAsync();
            try { using var unexpected = await interrupted; Assert.Fail("被终止的上传不得返回成功。"); }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "files-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Directory.EnumerateFiles(root, "v1-*").Count() != 2) { await Task.Delay(50, timeout.Token); }
            Assert.Empty(Directory.EnumerateFiles(root, "*.part"));
            using var preserved = await restarted.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
            Assert.Equal(new byte[] { 1, 2 }, await preserved.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [PostgresFact]
    public async Task ProcessDiesDuringCommit_RecoveryKeepsBytesForAnyCommittedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-unknown-commit-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            await using var observer = new NpgsqlConnection(database.ConnectionString);
            await observer.OpenAsync();
            await using (var setup = new NpgsqlCommand("""
                CREATE FUNCTION files.hold_test_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Name" = 'commit-unknown.bin' THEN
                        PERFORM pg_advisory_xact_lock(360036, 1);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER hold_test_commit AFTER INSERT ON files.stored_files
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION files.hold_test_commit();
                """, observer))
            {
                await setup.ExecuteNonQueryAsync();
            }
            await using var blocker = new NpgsqlConnection(database.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(360036, 1)", blocker, transaction))
            {
                await hold.ExecuteNonQueryAsync();
            }
            await using var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            using var uploadClient = new HttpClient { BaseAddress = first.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            await PlatformSettingsAccessTests.LoginAsync(uploadClient, "journey-root", "files-root-password");
            byte[] bytes = [0, 128, 255, 17, 33];
            using var content = new ByteArrayContent(bytes);
            var upload = uploadClient.PostAsync(new Uri("/api/files?name=commit-unknown.bin", UriKind.Relative), content);
            await WaitForDatabaseSignalAsync(observer, """
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND query ILIKE 'COMMIT%' AND wait_event = 'advisory')
                """);
            var key = Path.GetFileName(Assert.Single(Directory.EnumerateFiles(root, "v1-*"),
                path => Path.GetExtension(path).Length == 0));
            await first.CrashAsync();
            await Assert.ThrowsAsync<HttpRequestException>(async () => { using var response = await upload; });
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "files-root-password");
            try
            {
                // 数据库仍可能执行原 COMMIT。等待恢复者到达这道锁，或数据库已经回滚且完成退役。
                await using var recovery = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                        AND query LIKE '%hashtextextended%' AND wait_event = 'advisory')
                        OR EXISTS (SELECT 1 FROM files.retired_storage_keys WHERE "StorageKey" = @key)
                    """, observer);
                recovery.Parameters.AddWithValue("key", key);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!(bool)(await recovery.ExecuteScalarAsync(timeout.Token))!) { await Task.Delay(50, timeout.Token); }
            }
            finally { await transaction.RollbackAsync(); }
            await WaitForDatabaseSignalAsync(observer, """
                SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND query ILIKE 'COMMIT%' AND state = 'active')
                """);
            // 提交结果只能由数据库确定；断开 HTTP 连接不能作为回滚的证据。
            await using var outcome = new NpgsqlCommand("SELECT \"Id\" FROM files.stored_files WHERE \"Name\" = 'commit-unknown.bin'", observer);
            var committedId = await outcome.ExecuteScalarAsync();
            if (committedId is long id)
            {
                using var download = await restarted.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories).Any()) { await Task.Delay(50, timeout.Token); }
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task WaitForDatabaseSignalAsync(NpgsqlConnection connection, string sql)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var command = new NpgsqlCommand(sql, connection);
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!) { await Task.Delay(50, timeout.Token); }
    }

    [PostgresFact]
    public async Task RejectedMetadataCommit_LeavesNoPublishedFile_AndRecoversOrphanBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-orphan-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var reject = new NpgsqlCommand("ALTER TABLE files.stored_files ADD CONSTRAINT reject_test_file CHECK (\"Name\" <> 'reject.bin')", connection);
                await reject.ExecuteNonQueryAsync();
            }
            await using var host = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(host.Client, "journey-root", "files-root-password");
            using var content = new ByteArrayContent([0, 128, 255]);
            using var rejected = await host.Client.PostAsync(new Uri("/api/files?name=reject.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories).Any())
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [PostgresFact]
    public Task AcceptedDeletion_ResumesAfterStorageRecoveryAndProcessRestart() => AssertDeletionRecoversAsync(false);

    [PostgresFact]
    public Task ReplacedStorageDirectory_DoesNotFalselyConfirmDeletion() => AssertDeletionRecoversAsync(true);

    private async Task AssertDeletionRecoversAsync(bool emptyReplacementDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-delete-" + Guid.NewGuid().ToString("N"));
        var unavailable = root + "-offline";
        try
        {
            await using var database = await databases.CreateAsync();
            long id;
            await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root))
            {
                await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "files-root-password");
                using var content = new ByteArrayContent([0, 128, 255]);
                using var uploaded = await first.Client.PostAsync(new Uri("/api/files?name=delete.bin", UriKind.Relative), content);
                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
                id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                // 只改变本测试的临时目录，模拟存储挂载不可用，保留实际字节供恢复验证。
                Directory.Move(root, unavailable);
                if (emptyReplacementDirectory) { Directory.CreateDirectory(root); }
                else { await File.WriteAllTextAsync(root, "storage unavailable"); }
                using var unavailableDownload = await first.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableDownload.StatusCode);
                var problem = await unavailableDownload.Content.ReadAsStringAsync();
                Assert.DoesNotContain(Path.GetFileName(root), problem, StringComparison.Ordinal);
                using var deleted = await first.Client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
                Assert.Equal($"/api/files/{id}/deletion", deleted.Headers.Location?.OriginalString);
                using var pending = await first.Client.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
                Assert.False((await pending.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean());
                using var hidden = await first.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                Assert.NotEmpty(Directory.EnumerateFiles(unavailable, "v1-*", SearchOption.AllDirectories));
            }
            if (emptyReplacementDirectory)
            {
                await using (var wrongStorage = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root))
                {
                    await PlatformSettingsAccessTests.LoginAsync(wrongStorage.Client, "journey-root", "files-root-password");
                    using var stillPending = await wrongStorage.Client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                    Assert.Equal(HttpStatusCode.Accepted, stillPending.StatusCode);
                    Assert.NotEmpty(Directory.EnumerateFiles(unavailable, "v1-*"));
                }
                Directory.Delete(root, recursive: true);
            }
            else { File.Delete(root); }
            Directory.Move(unavailable, root);
            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "files-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var status = await restarted.Client.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                if ((await status.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean()) { break; }
                await Task.Delay(50, timeout.Token);
            }
            Assert.Empty(Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories));
            using var repeated = await restarted.Client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        }
        finally
        {
            if (File.Exists(root)) { File.Delete(root); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            if (Directory.Exists(unavailable)) { Directory.Delete(unavailable, recursive: true); }
        }
    }

    [PostgresFact]
    public async Task UnmigratedFilesDatabase_RefusesStartup_UntilIndependentMigrationRuns()
    {
        await using var identity = await IdentityJourneyDatabase.CreateAsync();
        await identity.MigrateAsync();
        await using var files = await IdentityJourneyDatabase.CreateAsync();
        await using (var app = new PersistentIdentityApp(identity.ConnectionString, filesConnectionString: files.ConnectionString))
        {
            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
            Assert.Contains("migrate-files", error.ToString(), StringComparison.Ordinal);
        }
        // 重复启动仍拒绝，证明上一次启动没有悄悄应用迁移。
        await using (var repeated = new PersistentIdentityApp(identity.ConnectionString, filesConnectionString: files.ConnectionString))
        {
            var error = Assert.ThrowsAny<Exception>(() => repeated.CreateClient());
            Assert.Contains("migrate-files", error.ToString(), StringComparison.Ordinal);
        }
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var migration = await IdentityJourneyDatabase.RunMigrationAsync(files.ConnectionString, "Files");
            Assert.Equal(0, migration.ExitCode);
            Assert.Equal("Files migrations applied.", migration.Output.Trim());
        }
        await using var migrated = new PersistentIdentityApp(identity.ConnectionString, filesConnectionString: files.ConnectionString);
        using var client = migrated.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [PostgresFact]
    public async Task FilesDatabaseOutage_ChangesReadiness_WithoutStoppingIdentity_AndRecovers()
    {
        await using var identity = await databases.CreateAsync();
        await using var files = await IdentityJourneyDatabase.CreateAsync();
        var migration = await IdentityJourneyDatabase.RunMigrationAsync(files.ConnectionString, "Files");
        Assert.Equal(0, migration.ExitCode);
        await using var app = new PersistentIdentityApp(identity.ConnectionString, "files-root-password", filesConnectionString: files.ConnectionString);
        using var client = app.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await files.SetAvailableAsync(false);
        try
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var down = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-root-password");
        }
        finally { await files.SetAvailableAsync(true); }
        using var recovered = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [PostgresFact]
    public async Task UploadedPrivateFile_RemainsOwnedAndDownloadable_AfterProcessRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-files-persistence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await databases.CreateAsync();
            byte[] bytes = [0, 128, 255, 13, 10, 42];
            var beforeUpload = DateTimeOffset.UtcNow;
            string originalAudit;
            long id;
            await using (var first = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root))
            {
                await PlatformSettingsAccessTests.LoginAsync(first.Client, "journey-root", "files-root-password");
                using var content = new ByteArrayContent(bytes);
                using var uploaded = await first.Client.PostAsync(new Uri("/api/files?name=restart.bin", UriKind.Relative), content);
                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
                id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                using var initialMetadata = await first.Client.GetAsync(new Uri($"/api/files/{id}/metadata", UriKind.Relative));
                var audit = (await initialMetadata.Content.ReadApiDataAsync()).GetProperty("audit");
                Assert.InRange(audit.GetProperty("createdAt").GetDateTimeOffset(), beforeUpload, DateTimeOffset.UtcNow);
                Assert.False(string.IsNullOrEmpty(audit.GetProperty("createdBy").GetString()));
                Assert.Equal(JsonValueKind.Null, audit.GetProperty("updatedAt").ValueKind);
                originalAudit = audit.GetRawText();
            }

            await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root);
            await PlatformSettingsAccessTests.LoginAsync(restarted.Client, "journey-root", "files-root-password");
            using var metadata = await restarted.Client.GetAsync(new Uri($"/api/files/{id}/metadata", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            var file = await metadata.Content.ReadApiDataAsync();
            Assert.Equal(originalAudit, file.GetProperty("audit").GetRawText());
            Assert.Equal("restart.bin", file.GetProperty("name").GetString());
            Assert.Equal(6, file.GetProperty("size").ReadHttpInt64());
            using var download = await restarted.Client.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}
