using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.BuildingBlocks.Infrastructure.Ids;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.IntegrationSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(PlatformJourneyDefinition.Name)]
public sealed class FilesCommittedAuditTests(PlatformJourneyTemplate databases)
{
    [PostgresFact]
    public async Task DeletionOrigin_RollsBackWithItsFact_AndOnlyTheSuccessfulFirstDeleteOwnsIt()
    {
        await using var database = await databases.CreateAsync();
        await using var provider = BuildStorage(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var file = StoredFile.Register(new StoredFileId(76300), FileName.Create("origin-rollback.bin").Value,
            "application/octet-stream", "private-owner", DateTimeOffset.UtcNow).Value;
        await files.SaveAsync(file);
        Assert.True(file.Delete().IsSuccess);
        var operation = Guid.NewGuid();
        var failedOrigin = new ExecutionOrigin(operation, "platform", operation, "platform", "actor-a", "trace-a");
        var winningOrigin = failedOrigin with { OperationId = Guid.NewGuid(), InitiatorId = "actor-b" };
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION files.reject_deletion_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Payload"::jsonb->>'operation' = 'deletion-requested' THEN
                    RAISE EXCEPTION 'Injected deletion fact failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_deletion_fact BEFORE INSERT ON files.outbox
            FOR EACH ROW EXECUTE FUNCTION files.reject_deletion_fact();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => files.SaveAsync(file, 1, failedOrigin));
            Assert.NotNull(await files.FindAsync(file.Id));
            Assert.Null(await files.ReadDeletionOriginAsync(file.Id));
            await using (var recover = new NpgsqlCommand("DROP TRIGGER reject_deletion_fact ON files.outbox", connection))
            { await recover.ExecuteNonQueryAsync(); }
            await files.SaveAsync(file, 1, winningOrigin);
            Assert.Equal(winningOrigin, await files.ReadDeletionOriginAsync(file.Id));
            file.PostponeCleanup(DateTimeOffset.UtcNow.AddMinutes(1));
            await files.SaveAsync(file, 2, failedOrigin);
            await files.SaveAsync(file, 3);
            Assert.Equal(winningOrigin, await files.ReadDeletionOriginAsync(file.Id));
            await Assert.ThrowsAsync<FileMetadataConflictException>(() => files.SaveAsync(file, 1, failedOrigin));
            Assert.Equal(winningOrigin, await files.ReadDeletionOriginAsync(file.Id));

            var unknown = StoredFile.Register(new StoredFileId(76301), FileName.Create("unknown-origin.bin").Value,
                "application/octet-stream", "private-owner", DateTimeOffset.UtcNow).Value;
            await files.SaveAsync(unknown);
            unknown.Delete();
            await files.SaveAsync(unknown, 1);
            unknown.PostponeCleanup(DateTimeOffset.UtcNow.AddMinutes(1));
            await files.SaveAsync(unknown, 2, winningOrigin);
            Assert.Null(await files.ReadDeletionOriginAsync(unknown.Id));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DROP TRIGGER IF EXISTS reject_deletion_fact ON files.outbox;
                DROP FUNCTION IF EXISTS files.reject_deletion_fact();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [AuditBrokerFact]
    public async Task DeferredDeletion_RestartsAsSystemRecovery_AndRetainsTheFirstDeletionOrigin()
    {
        await using var database = await databases.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "nsn-file-origin-" + Guid.NewGuid().ToString("N"));
        var unavailable = root + "-offline";
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-files", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = StoredFileCommittedV1.Name, ConsumerName = prefix + "-files" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            long id;
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root))
            {
                await PlatformSettingsAccessTests.LoginAsync(source.Client, "journey-root", "files-root-password");
                using var bytes = new ByteArrayContent([1, 2, 3]);
                using var upload = await source.Client.PostAsync(new Uri("/api/files?name=private-recovery.bin", UriKind.Relative), bytes);
                Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
                id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                // 两个路径都由本测试的唯一临时根生成，保留字节以模拟挂载恢复。
                Directory.Move(root, unavailable);
                await File.WriteAllTextAsync(root, "storage unavailable");
                foreach (var correlation in new[] { "first-file-deletion", "repeated-file-deletion" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/files/{id}", UriKind.Relative));
                    request.Headers.Add("X-Correlation-ID", correlation);
                    using var deleted = await source.Client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
                }
                await source.CrashAsync();
            }
            File.Delete(root);
            Directory.Move(unavailable, root);
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password", root,
                settings: AuditBusinessJourneyTests.Settings(broker, prefix));
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "files-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/entries?source=files&subjectId={id}", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                facts = (await response.Content.ReadApiDataAsync()).EnumerateArray()
                    .Select(entry => entry.GetProperty("fact").Clone()).ToArray();
                if (facts.Any(fact => fact.GetProperty("action").GetString() == "files.stored-file.bytes-removed")
                    && facts.Any(fact => fact.GetProperty("action").GetString() == "files.stored-file.deletion-requested")) { break; }
                await Task.Delay(100, timeout.Token);
            }
            var deletion = Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "files.stored-file.deletion-requested");
            var removal = Assert.Single(facts, fact => fact.GetProperty("action").GetString() == "files.stored-file.bytes-removed");
            Assert.Equal("first-file-deletion", deletion.GetProperty("correlationId").GetString());
            Assert.True(removal.TryGetProperty("execution", out var execution) && execution.ValueKind == JsonValueKind.Object,
                "重启后的清理事实必须关联独立的后台执行及原删除请求。");
            var origin = deletion.GetProperty("execution");
            Assert.Equal(origin.GetProperty("rootOperationId").GetGuid(), execution.GetProperty("rootOperationId").GetGuid());
            Assert.Equal(deletion.GetProperty("actorId").GetString(), execution.GetProperty("initiatorId").GetString());
            Assert.Equal("first-file-deletion", removal.GetProperty("correlationId").GetString());
            Assert.Equal(JsonValueKind.Null, removal.GetProperty("actorId").ValueKind);
            Assert.NotEqual(origin.GetProperty("operationId").GetGuid(), execution.GetProperty("operationId").GetGuid());
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri($"/api/auditing/operations?operationId={execution.GetProperty("operationId").GetGuid()}&outcome=completed", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var found = (await response.Content.ReadApiDataAsync()).EnumerateArray().ToArray();
                if (found.Length == 0) { await Task.Delay(100, timeout.Token); continue; }
                var operation = Assert.Single(found);
                Assert.Equal("recovery", operation.GetProperty("kind").GetString());
                Assert.Equal(JsonValueKind.Null, operation.GetProperty("actorId").ValueKind);
                var metadata = operation.GetProperty("metadata");
                Assert.Equal("files.deletion.recover", metadata.GetProperty("action").GetString());
                Assert.Equal("stored-file", metadata.GetProperty("subjectType").GetString());
                Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.GetProperty("subjectId").GetString());
                Assert.Equal(origin.GetProperty("operationId").GetGuid(), metadata.GetProperty("parentOperationId").GetGuid());
                Assert.Equal(deletion.GetProperty("actorId").GetString(), metadata.GetProperty("initiatorId").GetString());
                break;
            }
            Assert.Empty(Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories));
        }
        finally
        {
            await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology);
            if (File.Exists(root)) { File.Delete(root); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            if (Directory.Exists(unavailable)) { Directory.Delete(unavailable, recursive: true); }
        }
    }

    [PostgresFact]
    public async Task StaleFileUpdate_DoesNotOverwriteDeletion_OrPublishAnUncommittedFact()
    {
        await using var database = await databases.CreateAsync();
        await using var provider = BuildStorage(database.ConnectionString);
        var id = new StoredFileId(76200);
        await using (var create = provider.CreateAsyncScope())
        {
            var file = StoredFile.Register(id, FileName.Create("private-concurrent.bin").Value,
                "application/octet-stream", "private-owner", DateTimeOffset.UtcNow).Value;
            Assert.True(file.MarkStored("private-winning-key", 3).IsSuccess);
            await create.ServiceProvider.GetRequiredService<IStoredFileRepository>().SaveAsync(file);
        }
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var winner = first.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var stale = second.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var deleting = await winner.FindAsync(id);
        var replacing = await stale.FindAsync(id);
        Assert.NotNull(deleting);
        Assert.NotNull(replacing);
        Assert.True(deleting.Delete().IsSuccess);
        Assert.True(replacing.MarkStored("private-losing-key", 4).IsSuccess);
        await winner.SaveAsync(deleting, 2);
        await Assert.ThrowsAsync<FileMetadataConflictException>(() => stale.SaveAsync(replacing, 2));
        var current = await stale.FindDeletedAsync(id);
        Assert.NotNull(current);
        Assert.Equal("private-winning-key", current.StorageKey);
        Assert.Equal(3, current.Version);
        var serializer = second.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await second.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<StoredFileCommittedV1>(entry.Payload)).ToArray();
        Assert.Equal(3, facts.Length);
        Assert.Equal("deletion-requested", Assert.Single(facts, fact => fact.Version == 3).Operation);
        Assert.Single(facts, fact => fact.Operation == "stored");
    }

    [AuditBrokerFact]
    public async Task FileFacts_SurviveSourceRestart_AndReachCentralInvestigationThroughRabbitMq()
    {
        await using var database = await databases.CreateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-files", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = StoredFileCommittedV1.Name, ConsumerName = prefix + "-files" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            long id;
            await using (var source = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password"))
            {
                await PlatformSettingsAccessTests.LoginAsync(source.Client, "journey-root", "files-root-password");
                source.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "file-facts-restarted");
                using var bytes = new ByteArrayContent([1, 2, 3]);
                using var upload = await source.Client.PostAsync(new Uri("/api/files?name=private-restarted.bin", UriKind.Relative), bytes);
                Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
                id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                using var deleted = await source.Client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                await source.CrashAsync();
            }
            await using var resumed = await PlatformHostProcess.StartAsync(database.ConnectionString, "files-root-password",
                settings: AuditBusinessJourneyTests.Settings(broker, prefix));
            await PlatformSettingsAccessTests.LoginAsync(resumed.Client, "journey-root", "files-root-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonElement[] facts;
            while (true)
            {
                using var response = await resumed.Client.GetAsync(new Uri("/api/auditing/entries?source=files&correlationId=file-facts-restarted", UriKind.Relative), timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                facts = page.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("fact").Clone())
                    .OrderBy(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()).ThenBy(fact => fact.GetProperty("action").GetString(), StringComparer.Ordinal).ToArray();
                Assert.True(facts.Length <= 4, "重复交付不能增加文件事实。");
                if (facts.Length == 4) { break; }
                await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(new[] { "files.stored-file.registered", "files.stored-file.stored", "files.stored-file.deletion-requested", "files.stored-file.bytes-removed" },
                facts.Select(fact => fact.GetProperty("action").GetString()));
            Assert.Equal(new long[] { 2, 2, 3, 4 }, facts.Select(fact => fact.GetProperty("subjectVersion").ReadHttpInt64()));
            Assert.All(facts, fact =>
            {
                Assert.Equal("stored-file", fact.GetProperty("subjectType").GetString());
                Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.GetProperty("subjectId").GetString());
                Assert.Equal("platform", fact.GetProperty("execution").GetProperty("source").GetString());
                Assert.DoesNotContain("private", fact.GetRawText(), StringComparison.OrdinalIgnoreCase);
            });
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    [PostgresFact]
    public async Task SourceFactFailure_RollsBackMetadataAndTheWholeFactBatch_AndTheSameScopeCanRetry()
    {
        await using var database = await databases.CreateAsync();
        await using var provider = BuildStorage(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
        var file = StoredFile.Register(new StoredFileId(76100), FileName.Create("private-retry.bin").Value,
            "application/octet-stream", "private-owner", DateTimeOffset.UtcNow).Value;
        Assert.True(file.MarkStored("private-retry-key", 3).IsSuccess);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION files.reject_stored_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Payload"::jsonb->>'operation' = 'stored' THEN
                    RAISE EXCEPTION 'Injected file fact failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_stored_fact BEFORE INSERT ON files.outbox
            FOR EACH ROW EXECUTE FUNCTION files.reject_stored_fact();
            """, connection)) { await inject.ExecuteNonQueryAsync(); }
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => files.SaveAsync(file));
            Assert.Null(await files.FindAsync(file.Id));
            Assert.Empty(await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow));
            await using (var recover = new NpgsqlCommand("DROP TRIGGER reject_stored_fact ON files.outbox", connection))
            { await recover.ExecuteNonQueryAsync(); }
            await files.SaveAsync(file);
            Assert.NotNull(await files.FindAsync(file.Id));
            var pending = await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow);
            var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
            Assert.Equal(new[] { "registered", "stored" }, pending.Select(entry => serializer.Deserialize<StoredFileCommittedV1>(entry.Payload).Operation).Order(StringComparer.Ordinal));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DROP TRIGGER IF EXISTS reject_stored_fact ON files.outbox;
                DROP FUNCTION IF EXISTS files.reject_stored_fact();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task CleanupDeferral_IsACommittedFact_AndRepeatedStateDoesNotAddFacts()
    {
        await using var database = await databases.CreateAsync();
        await using var provider = BuildStorage(database.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var at = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var file = StoredFile.Register(new StoredFileId(76000), FileName.Create("private-lifecycle.bin").Value,
            "application/octet-stream", "private-owner", at).Value;
        await files.SaveAsync(file);
        Action<StoredFile>[] mutations =
        [
            item => item.MarkStored("private-storage-key", 3),
            item => item.Delete(),
            item => item.PostponeCleanup(at.AddMinutes(1)),
            item => item.PostponeCleanup(at.AddMinutes(2)),
            item => item.ConfirmBytesRemoved(at.AddMinutes(3)),
        ];
        foreach (var mutation in mutations)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var current = await files.FindAsync(file.Id) ?? await files.FindDeletedAsync(file.Id);
                Assert.NotNull(current);
                var originalVersion = current.Version;
                mutation(current);
                await files.SaveAsync(current, originalVersion);
            }
        }
        var serializer = scope.ServiceProvider.GetRequiredService<IIntegrationEventSerializer>();
        var pending = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey)
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry => serializer.Deserialize<StoredFileCommittedV1>(entry.Payload)).OrderBy(fact => fact.Version).ToArray();
        Assert.Equal(new[] { "registered", "stored", "deletion-requested", "cleanup-deferred", "cleanup-deferred", "bytes-removed" }, facts.Select(fact => fact.Operation));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, facts.Select(fact => fact.Version));
        var removed = await files.FindDeletedAsync(file.Id);
        Assert.NotNull(removed);
        Assert.Equal(at.AddMinutes(3).ToUnixTimeMilliseconds(), removed.BytesRemovedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Null(removed.NextCleanupAttemptAt);
        Assert.All(facts, fact => { Assert.Equal(76000, fact.FileId); Assert.Null(fact.ActorId); });
        Assert.All(pending, entry => Assert.DoesNotContain("private", entry.Payload, StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task UploadAndDelete_RecordSeparateCommittedLifecycleFacts_WithoutFileContent()
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, "files-root-password");
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, "journey-root", "files-root-password");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "file-lifecycle-facts");
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var upload = await client.PostAsync(new Uri("/api/files?name=private-fact-name.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var deleted = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        await using var read = app.Services.CreateAsyncScope();
        var pending = await read.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files")
            .ReadPendingAsync(100, DateTimeOffset.UtcNow);
        var facts = pending.Select(entry =>
        {
            using var document = JsonDocument.Parse(entry.Payload);
            return document.RootElement.Clone();
        }).OrderBy(fact => fact.GetProperty("version").GetInt64()).ThenBy(fact => fact.GetProperty("operation").GetString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "registered", "stored", "deletion-requested", "bytes-removed" }, facts.Select(fact => fact.GetProperty("operation").GetString()));
        Assert.Equal(new long[] { 2, 2, 3, 4 }, facts.Select(fact => fact.GetProperty("version").GetInt64()));
        Assert.All(facts, fact =>
        {
            Assert.Equal(id, fact.GetProperty("fileId").GetInt64());
            Assert.Equal("file-lifecycle-facts", fact.GetProperty("correlationId").GetString());
            Assert.Equal("platform", fact.GetProperty("execution").GetProperty("source").GetString());
            Assert.False(string.IsNullOrWhiteSpace(fact.GetProperty("actorId").GetString()));
            Assert.DoesNotContain("private-fact-name", fact.GetRawText(), StringComparison.Ordinal);
            Assert.False(fact.TryGetProperty("storageKey", out _));
        });
    }

    internal static ServiceProvider BuildStorage(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        services.AddNexusStackInfrastructure(new IdGeneratorOptions { WorkerId = 21 });
        services.AddFilesPostgresMetadata(connectionString);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
