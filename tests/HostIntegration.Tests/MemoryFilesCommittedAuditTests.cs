using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Events.RabbitMq;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFilesCommittedAuditTests
{
    [AuditBrokerFact]
    public async Task MemorySource_PublishesThroughHostComposition_AndCentralCanReceiveAfterSourceStops()
    {
        await using var central = await IdentityJourneyDatabase.CreateAsync();
        await central.MigrateAsync();
        var prefix = RabbitMqTestBroker.UniquePrefix();
        var broker = RabbitMqTestBroker.Options with { ExchangeName = prefix + "-memory-files", ClientName = prefix };
        var topology = EventTopology.Create(broker.ExchangeName,
        [
            new EventSubscription { EventName = StoredFileCommittedV1.Name, ConsumerName = prefix + "-files" },
            new EventSubscription { EventName = "platform.setting-committed.v1", ConsumerName = prefix },
        ]);
        try
        {
            Assert.True((await new RabbitMqTopologyBootstrapper(broker).ApplyAsync(RabbitTopologyPlanner.Plan(topology))).IsSuccess);
            var settings = AuditBusinessJourneyTests.Settings(broker, prefix);
            var sourceSettings = settings.ToDictionary(item => item.Key.Replace("__", ":", StringComparison.Ordinal), item => (string?)item.Value, StringComparer.Ordinal);
            sourceSettings["Auditing:Messaging:Enabled"] = "false";
            sourceSettings["Identity:Root:UserName"] = PlatformAppWithRootAccount.RootUserName;
            sourceSettings["Identity:Root:Password"] = PlatformAppWithRootAccount.RootPassword;
            long id;
            await using (var source = new MemorySourceApp(sourceSettings) { SchedulingWorkerEnabled = false })
            {
                using var client = source.CreateClient();
                await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
                using var bytes = new ByteArrayContent([4, 5, 6]);
                using var upload = await client.PostAsync(new Uri("/api/files?name=memory-broker.bin", UriKind.Relative), bytes);
                Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
                id = (await upload.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
                using var deleted = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                await using var scope = source.Services.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
                await GatewayResilienceTests.EventuallyAsync(async () => (await outbox.ReadPendingAsync(100, DateTimeOffset.MaxValue)).Count == 0);
            }
            // 内存来源已经结束；此后能到达中央的只能是已交给 broker 的消息。
            await using var reader = await PlatformHostProcess.StartAsync(central.ConnectionString, "memory-files-root", settings: settings);
            await PlatformSettingsAccessTests.LoginAsync(reader.Client, "journey-root", "memory-files-root");
            await GatewayResilienceTests.EventuallyAsync(async () =>
            {
                using var response = await reader.Client.GetAsync(new Uri($"/api/auditing/entries?source=files&subjectId={id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var facts = (await response.Content.ReadApiDataAsync()).EnumerateArray().Select(item => item.GetProperty("fact")).ToArray();
                if (facts.Length != 4) { return false; }
                Assert.Equal(new[] { "files.stored-file.bytes-removed", "files.stored-file.deletion-requested", "files.stored-file.registered", "files.stored-file.stored" },
                    facts.Select(item => item.GetProperty("action").GetString()).Order(StringComparer.Ordinal));
                Assert.All(facts, fact => Assert.DoesNotContain("memory-broker.bin", fact.GetRawText(), StringComparison.Ordinal));
                return true;
            });
        }
        finally { await AuditBusinessJourneyTests.DeleteTopologyAsync(broker, topology); }
    }

    private sealed class MemorySourceApp(Dictionary<string, string?> settings) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // 总线是否组装在 Program 执行时决定，配置必须在该决定之前可见。
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task SharedSnapshots_RejectLosingChanges_AndSystemRecoveryUsesItsOwnActorAndOriginalDeletionOrigin()
    {
        await using var baseApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ICurrentUser>(new FixedCurrentUser("ambient-user"))));
        await using var first = app.Services.CreateAsyncScope();
        await using var second = app.Services.CreateAsyncScope();
        var winner = first.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var loser = second.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var file = NewFile(76602);
        await winner.SaveAsync(file);
        var stale = (await loser.FindAsync(file.Id))!;
        file.Delete();
        var parent = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "original-user", "memory-recovery");
        await winner.SaveAsync(file, 1, parent);
        stale.MarkStored("private-losing-key", 4);
        await Assert.ThrowsAsync<FileMetadataConflictException>(() => loser.SaveAsync(stale, 1));
        Assert.Null((await loser.FindDeletedAsync(file.Id))!.StorageKey);
        await second.ServiceProvider.GetRequiredService<FileRecovery>().RunOnceAsync();
        var saved = (await winner.FindDeletedAsync(file.Id))!;
        Assert.Equal("ambient-user", saved.CreatedBy);
        Assert.Null(saved.UpdatedBy);
        Assert.NotNull(saved.BytesRemovedAt);
        var outbox = second.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
        var facts = await ReadFactsAsync(outbox);
        Assert.Equal(3, facts.Length);
        var removed = Assert.Single(facts, item => item.Operation == "bytes-removed");
        Assert.Null(removed.ActorId);
        Assert.Equal(parent.RootOperationId, removed.Execution!.RootOperationId);
        Assert.Equal("original-user", removed.Execution.InitiatorId);
        Assert.NotEqual(parent.OperationId, removed.Execution.OperationId);
        Assert.DoesNotContain(facts, item => item.Operation == "stored");
        var now = DateTimeOffset.UtcNow;
        var pending = await outbox.ReadPendingAsync(100, now);
        await outbox.MarkFailedAsync(pending[0].Id, "delivery.unavailable", now.AddHours(1), 0);
        Assert.Equal(2, (await outbox.ReadPendingAsync(100, now)).Count);
        Assert.Equal(3, (await outbox.ReadPendingAsync(100, now.AddHours(2))).Count);
        var otherOutbox = first.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
        await otherOutbox.MarkDeadLetteredAsync(pending[0].Id, "delivery.exhausted", now, 0);
        foreach (var entry in pending.Skip(1)) { await otherOutbox.MarkDeliveredAsync(entry.Id, now); }
        Assert.Empty(await outbox.ReadPendingAsync(100, now.AddDays(1)));
        Assert.Equal(3, (await loser.FindDeletedAsync(file.Id))!.Version);
    }

    [Fact]
    public async Task HttpLifecycle_StoresFactsAndRowAuditAcrossScopes_WithoutPublishingPrivateFileData()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "memory-file-lifecycle");
        using var bytes = new ByteArrayContent([1, 2, 3]);
        using var uploaded = await client.PostAsync(new Uri("/api/files?name=private-memory.bin", UriKind.Relative), bytes);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var before = (await files.FindAsync(new StoredFileId(id)))!;
        Assert.NotEqual(default, before.CreatedAt);
        Assert.False(string.IsNullOrWhiteSpace(before.CreatedBy));
        Assert.Null(before.UpdatedAt);
        await files.SaveAsync(before, before.Version);
        Assert.Equal(before.CreatedBy, (await files.FindAsync(before.Id))!.CreatedBy);
        Assert.Equal(2, (await ReadFactsAsync(outbox)).Length);
        using var deleted = await client.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var facts = (await ReadFactsAsync(outbox)).OrderBy(item => item.Version).ThenBy(item => item.Operation, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "registered", "stored", "deletion-requested", "bytes-removed" }, facts.Select(item => item.Operation));
        Assert.Equal(new long[] { 2, 2, 3, 4 }, facts.Select(item => item.Version));
        Assert.All(facts, fact =>
        {
            Assert.Equal(id, fact.FileId);
            Assert.Equal(before.CreatedBy, fact.ActorId);
            Assert.Equal("memory-file-lifecycle", fact.CorrelationId);
            Assert.Equal("platform", fact.Execution!.Source);
        });
        var saved = (await files.FindDeletedAsync(before.Id))!;
        Assert.Equal(before.CreatedAt, saved.CreatedAt);
        Assert.Equal(before.CreatedBy, saved.CreatedBy);
        Assert.Equal(before.CreatedBy, saved.UpdatedBy);
        Assert.NotNull(saved.UpdatedAt);
        foreach (var entry in await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow))
        {
            Assert.DoesNotContain("private-memory", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("storageKey", entry.Payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FactBatchFailureAndCancellation_PreserveMetadataAndFirstDeletionOrigin_AndSameScopeCanRetry()
    {
        var serializer = new InterruptibleFileSerializer();
        await using var baseApp = new MemoryFactCapacityApp("Files", 4) { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IIntegrationEventSerializer>(serializer)));
        await using var scope = app.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(FilesPersistenceServiceCollectionExtensions.OutboxKey);
        var file = NewFile(76601);
        file.MarkStored("private-memory-key", 3);
        serializer.BeforeSerialize = fact => { if (fact.Operation == "stored") { throw new InvalidOperationException("Injected second fact failure"); } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.SaveAsync(file));
        Assert.Null(await files.FindAsync(file.Id));
        Assert.Empty(await ReadFactsAsync(outbox));
        serializer.BeforeSerialize = null;
        await files.SaveAsync(file);
        Assert.Equal(2, (await ReadFactsAsync(outbox)).Length);
        Assert.Equal(2, (await files.FindAsync(file.Id))!.Version);

        var failedOrigin = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "first", "file-origin");
        var winner = failedOrigin with { OperationId = Guid.NewGuid(), InitiatorId = "winner" };
        file.Delete();
        using var cancellation = new CancellationTokenSource();
        serializer.BeforeSerialize = fact => { if (fact.Operation == "deletion-requested") { cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SaveAsync(file, 2, failedOrigin, cancellation.Token));
        Assert.NotNull(await files.FindAsync(file.Id));
        Assert.Null(await files.FindDeletedAsync(file.Id));
        Assert.Null(await files.ReadDeletionOriginAsync(file.Id));
        Assert.Equal(2, (await ReadFactsAsync(outbox)).Length);
        serializer.BeforeSerialize = null;
        await files.SaveAsync(file, 2, winner);
        file.PostponeCleanup(DateTimeOffset.UtcNow.AddMinutes(2));
        await files.SaveAsync(file, 3, failedOrigin);
        await files.SaveAsync(file, 4);
        Assert.Equal(winner, await files.ReadDeletionOriginAsync(file.Id));
        await Assert.ThrowsAsync<FileMetadataConflictException>(() => files.SaveAsync(file, 2, failedOrigin));
        Assert.Equal(4, (await ReadFactsAsync(outbox)).Length);
        Assert.Equal(winner, await files.ReadDeletionOriginAsync(file.Id));
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(NewFile(76603)));
    }

    private static StoredFile NewFile(long id) => StoredFile.Register(new StoredFileId(id), FileName.Create("private-file.bin").Value,
        "application/octet-stream", "owner", DateTimeOffset.UtcNow).Value;

    private static async Task<StoredFileCommittedV1[]> ReadFactsAsync(IOutboxStore outbox) =>
        (await outbox.ReadPendingAsync(100, DateTimeOffset.UtcNow)).Select(item => new SystemTextJsonIntegrationEventSerializer().Deserialize<StoredFileCommittedV1>(item.Payload)).ToArray();

    private sealed class InterruptibleFileSerializer : IIntegrationEventSerializer
    {
        private readonly SystemTextJsonIntegrationEventSerializer _inner = new();
        public Action<StoredFileCommittedV1>? BeforeSerialize { get; set; }
        public string Serialize(IntegrationEvent integrationEvent)
        {
            if (integrationEvent is StoredFileCommittedV1 file) { BeforeSerialize?.Invoke(file); }
            return _inner.Serialize(integrationEvent);
        }
        public TEvent Deserialize<TEvent>(string payload) where TEvent : IntegrationEvent => _inner.Deserialize<TEvent>(payload);
    }
}
