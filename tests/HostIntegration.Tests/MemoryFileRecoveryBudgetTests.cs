using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class MemoryFileRecoveryBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedDeletion_LeavesRecoverablePendingState_WhenCompletionIsBusyOrCancelled(bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-memory-file-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var clock = new PausingClock(DateTimeOffset.UtcNow);
            await using var provider = BuildMetadata(clock);
            await using var scope = provider.CreateAsyncScope();
            using var disk = new LocalDiskFileStore(root);
            disk.EnsureCreated();
            var storage = new PausingFileStore(disk);
            var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var recovery = new FileRecovery(storage, files, clock, new FileRecoveryOptions(), disk);
            var service = new FileService(storage, files, new SequentialIdGenerator(), clock, new FileUploadLimits(), recovery);
            using var content = new MemoryStream([9, 8, 7]);
            var upload = await service.UploadAsync(FileName.Create("pending.bin").Value, "application/octet-stream", content, "owner");
            Assert.True(upload.IsSuccess);
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
            var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
            var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files");
            using var cancellation = new CancellationTokenSource();
            storage.PauseNextDelete();
            var deletion = service.DeleteAsync(upload.Value.Id, "owner", cancellation.Token);
            Task<int>? holder = null;
            try
            {
                Assert.Equal(upload.Value.StorageKey, await storage.Paused.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(await files.FindAsync(upload.Value.Id));
                var accepted = Assert.IsType<StoredFile>(await files.FindDeletedAsync(upload.Value.Id));
                Assert.Equal(3, accepted.Version);
                Assert.Null(accepted.BytesRemovedAt);
                Assert.Equal(3, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
                holder = Task.Run(() =>
                {
                    clock.PauseNextReadOnCurrentThread();
                    return cleanup.CleanupAsync();
                });
                await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));
                storage.Resume();
                if (cancel)
                {
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await deletion.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else
                {
                    var deferred = await deletion.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.True(deferred.IsSuccess);
                    Assert.False(deferred.Value);
                }
            }
            finally
            {
                storage.Resume();
                clock.Resume();
                if (holder is not null) { await holder; }
                try { await deletion; }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            }
            var pending = Assert.Single(await files.PendingDeletionsAsync(clock.UtcNow, 10));
            Assert.Equal(3, pending.Version);
            Assert.Null(pending.BytesRemovedAt);
            Assert.Equal(3, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
            Assert.Equal(3, (await capacity.ReadAsync()).Value.RetainedRecords);
            Assert.False(await service.DeletionCompletedAsync(upload.Value.Id, "owner"));
            await Assert.ThrowsAnyAsync<IOException>(() => disk.OpenReadAsync(upload.Value.StorageKey!));
            Assert.True((await service.DeleteAsync(upload.Value.Id, "owner")).Value);
            Assert.True(await service.DeletionCompletedAsync(upload.Value.Id, "owner"));
            Assert.Empty(await files.PendingDeletionsAsync(clock.UtcNow, 10));
            Assert.Equal(4, (await files.FindDeletedAsync(upload.Value.Id))!.Version);
            Assert.Equal(4, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
            Assert.Equal(4, (await capacity.ReadAsync()).Value.RetainedRecords);
            Assert.True((await service.DeleteAsync(upload.Value.Id, "owner")).Value);
            Assert.Equal(4, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
        }
        finally { DeleteOwnedRoot(root); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RejectedUpload_PreservesProtectedBytes_AndRetiresOrphanOnlyAfterRecovery(bool cancelUpload, bool cancelRetirement)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-memory-file-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var clock = new PausingClock(DateTimeOffset.UtcNow);
            await using var provider = BuildMetadata(clock);
            await using var scope = provider.CreateAsyncScope();
            using var disk = new LocalDiskFileStore(root);
            disk.EnsureCreated();
            var storage = new PausingFileStore(disk);
            var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            var recovery = new FileRecovery(storage, files, clock, new FileRecoveryOptions(), disk);
            var service = new FileService(storage, files, new SequentialIdGenerator(), clock, new FileUploadLimits(maxConcurrentUploads: 1), recovery);
            var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>("files");
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("files");
            var capacity = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityReader>("files");
            var scanCutoff = clock.UtcNow.AddDays(1);
            using var uploadCancellation = new CancellationTokenSource();
            using var retirementCancellation = new CancellationTokenSource();
            using var content = new MemoryStream([9, 8, 7]);
            storage.PauseNextWrite();
            var upload = service.UploadAsync(FileName.Create("orphan.bin").Value, "application/octet-stream", content, "owner", uploadCancellation.Token);
            Task<int>? holder = null;
            Task? retirement = null;
            string key;
            try
            {
                key = await storage.Paused.WaitAsync(TimeSpan.FromSeconds(5));
                holder = Task.Run(() =>
                {
                    clock.PauseNextReadOnCurrentThread();
                    return cleanup.CleanupAsync();
                });
                await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));
                // Even an old candidate cannot reach the busy metadata gate while its upload protection is held.
                await disk.CollectOrphansAsync(files.RetireUnreferencedStorageAsync, scanCutoff, 10);
                storage.Resume();
                if (cancelUpload)
                {
                    uploadCancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await upload.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else { Assert.Equal("audit_capacity.busy", (await upload.WaitAsync(TimeSpan.FromSeconds(2))).Error.Code); }
                await AssertBytesAsync(disk, key);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                retirement = Task.Run(async () =>
                {
                    started.SetResult();
                    await disk.CollectOrphansAsync(files.RetireUnreferencedStorageAsync, scanCutoff, 10, retirementCancellation.Token);
                });
                await started.Task;
                if (cancelRetirement)
                {
                    retirementCancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await retirement.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else { await Assert.ThrowsAsync<CommittedFactCapacityBusyException>(async () => await retirement.WaitAsync(TimeSpan.FromSeconds(2))); }
                await AssertBytesAsync(disk, key);
            }
            finally
            {
                storage.Resume();
                clock.Resume();
                if (holder is not null) { await holder; }
                try { await upload; }
                catch (OperationCanceledException) when (cancelUpload && uploadCancellation.IsCancellationRequested) { }
                if (retirement is not null)
                {
                    try { await retirement; }
                    catch (Exception error) when (error is CommittedFactCapacityBusyException or OperationCanceledException) { }
                }
            }
            Assert.Null(await files.FindAsync(new StoredFileId(1001)));
            Assert.Empty(await outbox.ReadPendingAsync(10, clock.UtcNow));
            Assert.Equal(0, (await capacity.ReadAsync()).Value.RetainedRecords);
            Assert.Equal(0, (await capacity.ReadAsync()).Value.RetainedPayloadBytes);
            await disk.CollectOrphansAsync(files.RetireUnreferencedStorageAsync, scanCutoff, 10);
            await Assert.ThrowsAnyAsync<IOException>(() => disk.OpenReadAsync(key));
            var late = StoredFile.Register(new StoredFileId(1001), FileName.Create("orphan.bin").Value,
                "application/octet-stream", "owner", clock.UtcNow).Value;
            Assert.True(late.MarkStored(key, 3).IsSuccess);
            await Assert.ThrowsAsync<InvalidOperationException>(() => files.SaveAsync(late));
            Assert.Null(await files.FindAsync(late.Id));
            Assert.Equal(0, (await capacity.ReadAsync()).Value.RetainedRecords);
            using var retryContent = new MemoryStream([9, 8, 7]);
            var retry = await service.UploadAsync(FileName.Create("orphan.bin").Value, "application/octet-stream", retryContent, "owner");
            Assert.True(retry.IsSuccess);
            Assert.NotEqual(key, retry.Value.StorageKey);
            await disk.CollectOrphansAsync(files.RetireUnreferencedStorageAsync, scanCutoff, 10);
            await AssertBytesAsync(disk, retry.Value.StorageKey!);
            Assert.Equal(2, (await outbox.ReadPendingAsync(10, clock.UtcNow)).Count);
            Assert.Equal(2, (await capacity.ReadAsync()).Value.RetainedRecords);
        }
        finally { DeleteOwnedRoot(root); }
    }

    private static async Task AssertBytesAsync(LocalDiskFileStore storage, string key)
    {
        await using var read = await storage.OpenReadAsync(key);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(new byte[] { 9, 8, 7 }, copy.ToArray());
    }

    private static ServiceProvider BuildMetadata(IClock clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton<IIntegrationEventSerializer, SystemTextJsonIntegrationEventSerializer>();
        services.AddFilesInMemoryMetadata(write: new CommittedFactCapacityWriteOptions { Timeout = TimeSpan.FromMilliseconds(150) });
        services.AddFilesMemoryFactCleanup(new CommittedFactCleanupOptions { Enabled = false });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static void DeleteOwnedRoot(string root)
    {
        var target = Path.GetFullPath(root);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, target, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("nsn-memory-file-budget-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
    }
}
