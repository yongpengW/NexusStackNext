using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFilesFactCapacityTests
{
    [Fact]
    public async Task Utf8Limits_AdmitOnlyWholeBatches_AndFailedBatchesLeaveTheFullRemainder()
    {
        var now = DateTimeOffset.UtcNow;
        var serializer = new KnownPayloadSerializer { Payload = "中文" };
        var files = new InMemoryStoredFileRepository(serializer, new FixedClock(now),
            new() { MaxRecords = 10, MaxPayloadBytes = 7, MaxRecordPayloadBytes = 3 });
        var first = NewFile(99611, now);
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(first));
        Assert.Null(await files.FindAsync(first.Id));
        Assert.Empty(await files.ReadPendingAsync(10, now));
        Assert.Equal(default, first.CreatedAt);
        serializer.Payload = "中"; // Three UTF-8 bytes, independently known.
        await files.SaveAsync(first);

        var batch = NewFile(99612, now);
        Assert.True(batch.MarkStored("utf8-batch", 3).IsSuccess);
        serializer.Payload = "文";
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(batch));
        Assert.Null(await files.FindAsync(batch.Id));
        Assert.Equal(default, batch.CreatedAt);
        Assert.Equal("中", Assert.Single(await files.ReadPendingAsync(10, now)).Payload);
        serializer.Payload = "a";
        await files.SaveAsync(batch); // Two one-byte facts fit the four-byte remainder.
        await files.SaveAsync(batch, batch.Version);
        var last = NewFile(99613, now);
        serializer.Payload = "文";
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(last));
        Assert.Null(await files.FindAsync(last.Id));
        serializer.Payload = "ab";
        await files.SaveAsync(last); // Exact total: 3 + 1 + 1 + 2 = 7.
        Assert.Equal(new[] { "a", "a", "ab", "中" }, (await files.ReadPendingAsync(10, now))
            .Select(entry => entry.Payload).Order(StringComparer.Ordinal));
        serializer.Payload = "a";
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(NewFile(99614, now)));
        Assert.Null(await files.FindAsync(new StoredFileId(99614)));
    }

    [Fact]
    public async Task CapacityRefusal_PreservesWholeFileLifecycle_AndOnlyCommittedDeletionKeepsItsOrigin()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var app = new CapacityApp(clock) { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
        var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files");
        var seed = NewFile(99601, clock.UtcNow);
        Assert.True(seed.MarkStored("capacity-seed-storage", 3).IsSuccess);
        await files.SaveAsync(seed);
        var seedFacts = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, seedFacts.Count);
        var main = NewFile(99602, clock.UtcNow);
        Assert.True(main.MarkStored("capacity-main-storage", 3).IsSuccess);
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(main));
        Assert.Null(await files.FindAsync(main.Id));
        Assert.Equal(seedFacts, await outbox.ReadPendingAsync(10, clock.UtcNow));
        Assert.Equal(default, main.CreatedAt);

        foreach (var fact in seedFacts) { await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow); }
        Assert.Equal(0, await cleanup.CleanupAsync());
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        await files.SaveAsync(main);
        var storedFacts = await outbox.ReadPendingAsync(10, clock.UtcNow);
        Assert.Equal(2, storedFacts.Count);
        await files.SaveAsync(main, main.Version);
        Assert.Equal(storedFacts, await outbox.ReadPendingAsync(10, clock.UtcNow));
        var firstFiller = NewFile(99603, clock.UtcNow);
        await files.SaveAsync(firstFiller);
        Assert.True(main.Delete().IsSuccess);
        var refusedOrigin = new ExecutionOrigin(Guid.NewGuid(), "platform", Guid.NewGuid(), "platform", "first", "capacity-file-origin");
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(main, 2, refusedOrigin));
        Assert.Null(await files.ReadDeletionOriginAsync(main.Id));
        var stillVisible = await files.FindAsync(main.Id);
        Assert.NotNull(stillVisible);
        Assert.False(stillVisible.IsDeleted);
        Assert.Equal(2, stillVisible.Version);
        Assert.Equal(3, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);

        foreach (var fact in storedFacts) { await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow); }
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        var secondFiller = NewFile(99604, clock.UtcNow);
        await files.SaveAsync(secondFiller);
        var committedOrigin = refusedOrigin with { OperationId = Guid.NewGuid(), InitiatorId = "winner" };
        await files.SaveAsync(main, 2, committedOrigin);
        Assert.Null(await files.FindAsync(main.Id));
        Assert.Equal(committedOrigin, await files.ReadDeletionOriginAsync(main.Id));
        Assert.True(main.ConfirmBytesRemoved(clock.UtcNow).IsSuccess);
        await Assert.ThrowsAsync<FileAuditCapacityException>(() => files.SaveAsync(main, 3));
        var stillRecoverable = await files.FindDeletedAsync(main.Id);
        Assert.NotNull(stillRecoverable);
        Assert.Null(stillRecoverable.BytesRemovedAt);
        Assert.Equal(3, stillRecoverable.Version);
        Assert.Equal(committedOrigin, await files.ReadDeletionOriginAsync(main.Id));

        var serializer = new SystemTextJsonIntegrationEventSerializer();
        var pending = await outbox.ReadPendingAsync(10, clock.UtcNow);
        var fillerFacts = pending.Where(entry => serializer.Deserialize<StoredFileCommittedV1>(entry.Payload).FileId is 99603 or 99604).ToArray();
        Assert.Equal(2, fillerFacts.Length);
        foreach (var fact in fillerFacts) { await outbox.MarkDeliveredAsync(fact.Id, clock.UtcNow); }
        clock.UtcNow = clock.UtcNow.AddDays(8);
        Assert.Equal(2, await cleanup.CleanupAsync());
        await files.SaveAsync(main, 3);
        var completed = await files.FindDeletedAsync(main.Id);
        Assert.NotNull(completed);
        Assert.NotNull(completed.BytesRemovedAt);
        Assert.Equal(4, completed.Version);
        Assert.Equal(committedOrigin, await files.ReadDeletionOriginAsync(main.Id));
        Assert.Equal(new[] { "bytes-removed", "deletion-requested" }, (await outbox.ReadPendingAsync(10, clock.UtcNow))
            .Select(entry => serializer.Deserialize<StoredFileCommittedV1>(entry.Payload).Operation).Order(StringComparer.Ordinal));
    }

    private static StoredFile NewFile(long id, DateTimeOffset at)
        => StoredFile.Register(new StoredFileId(id), FileName.Create("private-capacity.bin").Value, "application/octet-stream", "owner", at).Value;

    private sealed class CapacityApp(MutableClock clock) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:AuditDelivery:MemoryCapacity:MaxRecords"] = "3",
                ["Files:AuditDelivery:Cleanup:Enabled"] = "false",
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        }
    }
}
