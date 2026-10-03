using System.Collections.Concurrent;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.TestSupport;

namespace NexusStackNext.Files.Application.Tests;

/// <summary>
/// 文件服务：**写入顺序与失败语义**。
/// <para>
/// "先写字节再标记已存储"这个设计在 HTTP 层面几乎验证不了——
/// 要让存储写入失败，得先让磁盘坏掉。这里用一个会失败的存储替身把它变成可测的。
/// </para>
/// </summary>
public sealed class FileServiceTests
{
    private const string Owner = "file-owner";
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static (FileService Service, FakeFileStore Store, InMemoryStoredFileRepository Files) NewService()
    {
        var store = new FakeFileStore();
        var files = new InMemoryStoredFileRepository();
        var clock = new FixedClock(Now);
        return (new FileService(store, files, new SequentialIdGenerator(7000), clock, new FileUploadLimits(),
            new FileRecovery(store, files, clock, new FileRecoveryOptions(), store)), store, files);
    }

    private static MemoryStream Content(string text = "hello") => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstDeletionOrigin_RemainsStableAcrossLaterRecoveryAndConflicts(bool knownOrigin)
    {
        var files = new InMemoryStoredFileRepository();
        var file = StoredFile.Register(new StoredFileId(7400), FileName.Create("origin.bin").Value, "application/octet-stream", Owner, Now).Value;
        var operation = Guid.NewGuid();
        var origin = new ExecutionOrigin(operation, "platform", operation, "platform", Owner, "origin-trace");
        var later = origin with { OperationId = Guid.NewGuid(), InitiatorId = "someone-else" };
        await files.SaveAsync(file);
        file.Delete();
        await files.SaveAsync(file, 1, knownOrigin ? origin : null);
        file.PostponeCleanup(Now.AddMinutes(1));
        await files.SaveAsync(file, 2, later);
        await files.SaveAsync(file, 3);
        await Assert.ThrowsAsync<FileMetadataConflictException>(() => files.SaveAsync(file, 1, later));
        Assert.Equal(knownOrigin ? origin : null, await files.ReadDeletionOriginAsync(file.Id));
    }

    [Fact]
    public async Task FailedMetadataUpdate_DoesNotHideThePreviouslyCommittedFile()
    {
        var repository = new RejectUpdatesRepository(new InMemoryStoredFileRepository());
        var store = new FakeFileStore();
        var clock = new FixedClock(Now);
        var service = new FileService(store, repository, new SequentialIdGenerator(7000), clock, new FileUploadLimits(),
            new FileRecovery(store, repository, clock, new FileRecoveryOptions(), store));
        using var content = Content();
        var uploaded = await service.UploadAsync(FileName.Create("kept.txt").Value, "text/plain", content, Owner);
        Assert.True(uploaded.IsSuccess);
        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(uploaded.Value.Id, Owner));
        var stillVisible = await service.DescribeAsync(uploaded.Value.Id, Owner);
        Assert.NotNull(stillVisible);
        var downloaded = await service.OpenAsync(uploaded.Value.Id, Owner);
        Assert.True(downloaded.IsSuccess);
        await downloaded.Value.Content.DisposeAsync();
    }

    private sealed class RejectUpdatesRepository(IStoredFileRepository inner) : IStoredFileRepository
    {
        public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<StoredFile?> FindDeletedAsync(StoredFileId id, CancellationToken cancellationToken = default) => inner.FindDeletedAsync(id, cancellationToken);
        public Task<ExecutionOrigin?> ReadDeletionOriginAsync(StoredFileId id, CancellationToken cancellationToken = default) => inner.ReadDeletionOriginAsync(id, cancellationToken);
        public Task<IReadOnlyList<StoredFile>> PendingDeletionsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            inner.PendingDeletionsAsync(now, limit, cancellationToken);
        public Task<bool> RetireUnreferencedStorageAsync(string storageKey, CancellationToken cancellationToken = default) =>
            inner.RetireUnreferencedStorageAsync(storageKey, cancellationToken);
        public Task SaveAsync(StoredFile file, long? originalVersion = null, ExecutionOrigin? deletionOrigin = null, CancellationToken cancellationToken = default) =>
            originalVersion is null ? inner.SaveAsync(file, cancellationToken: cancellationToken, deletionOrigin: deletionOrigin) : throw new IOException("Simulated metadata failure");
    }

    [Fact]
    public async Task Upload_WritesBytesThenSavesMetadata()
    {
        var (service, store, files) = NewService();

        var result = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content("hello"), Owner);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value.Size);
        Assert.Single(store.Written);
        Assert.Equal(result.Value.StorageKey, (await files.FindAsync(result.Value.Id))!.StorageKey);
    }

    [Fact]
    public async Task Upload_WhenTheStoreFails_LeavesNoMetadataBehind()
    {
        // 这是"先写字节再标记"这个顺序要防的那件事：
        // 反过来会留下"元数据说已存储、字节根本不在"，而它只会在**下载**时才以 500 暴露。
        var (service, store, files) = NewService();
        store.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(
            () => service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner));

        Assert.Null(await files.FindAsync(new StoredFileId(7001)));
    }

    [Fact]
    public async Task Upload_AcceptsANonSeekableStream_AndCountsActualBytes()
    {
        var (service, _, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", new NonSeekableStream(), Owner);
        Assert.True(uploaded.IsSuccess);
        Assert.Equal(0, uploaded.Value.Size);
    }

    [Fact]
    public async Task Upload_RejectsEmptyContentType()
    {
        var (service, store, files) = NewService();

        var result = await service.UploadAsync(FileName.Create("a.txt").Value, "  ", Content(), Owner);

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_type.empty", result.Error.Code);
        Assert.Empty(store.Written);
        Assert.Null(await files.FindAsync(new StoredFileId(7001)));
    }

    [Fact]
    public async Task Open_UnknownFile_Fails()
    {
        var (service, _, _) = NewService();

        var result = await service.OpenAsync(new StoredFileId(404), Owner);

        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Open_RegisteredButNeverStored_FailsWithContentMissing()
    {
        var (service, _, files) = NewService();
        var file = StoredFile.Register(new StoredFileId(1), FileName.Create("a.txt").Value, "text/plain", Owner, Now).Value;
        await files.SaveAsync(file);

        var result = await service.OpenAsync(new StoredFileId(1), Owner);

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_missing", result.Error.Code);
        Assert.DoesNotContain("测试注入", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_WhenTheStoreCannotFindTheBytes_FailsInsteadOfThrowing()
    {
        // 元数据在、字节不在——这是数据损坏或迁移出错后的状态。
        // 它必须以一个**明确的失败**返回，而不是把存储的异常直接抛给调用方：
        // 调用方拿到 Result 才能决定是 404、是 500、还是告警。
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner);
        store.FailReads = true;

        var result = await service.OpenAsync(uploaded.Value.Id, Owner);

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_missing", result.Error.Code);
        Assert.DoesNotContain("测试注入", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_SoftDeletedFile_LooksLikeItDoesNotExist()
    {
        var (service, _, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner);

        await service.DeleteAsync(uploaded.Value.Id, Owner);

        var result = await service.OpenAsync(uploaded.Value.Id, Owner);
        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Delete_SoftDeletesMetadataThenRemovesBytes()
    {
        var (service, store, files) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner);

        var result = await service.DeleteAsync(uploaded.Value.Id, Owner);

        Assert.True(result.IsSuccess);
        Assert.Single(store.Deleted);
        Assert.True((await files.FindDeletedAsync(uploaded.Value.Id))!.IsDeleted);
    }

    [Fact]
    public async Task Delete_WhenBytesCannotBeRemoved_ReportsPendingInsteadOfPretendingCompletion()
    {
        // 元数据已经软删了，字节删不掉——文件对用户已经不可见，但字节还在。
        // 已受理与清除完成必须可区分，不能拿 204 假装完成。
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner);
        store.FailDeletes = true;

        var result = await service.DeleteAsync(uploaded.Value.Id, Owner);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        Assert.False(await service.DeletionCompletedAsync(uploaded.Value.Id, Owner));
    }

    [Fact]
    public async Task Delete_UnknownFile_Fails()
    {
        var (service, _, _) = NewService();

        var result = await service.DeleteAsync(new StoredFileId(404), Owner);

        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Describe_ReturnsMetadataWithoutTouchingBytes()
    {
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content(), Owner);

        var described = await service.DescribeAsync(uploaded.Value.Id, Owner);

        Assert.NotNull(described);
        Assert.Equal("a.txt", described!.Name.Value);
        Assert.Empty(store.Reads);
    }
}

/// <summary>可配置失败的存储替身。用它把"磁盘坏掉"变成可测的输入。</summary>
internal sealed class FakeFileStore : IFileStore, IOrphanFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private int _nextKey;

    public bool FailWrites { get; set; }

    public bool FailReads { get; set; }

    public bool FailDeletes { get; set; }

    public List<string> Written { get; } = [];

    public List<string> Read { get; } = [];

    public List<string> Deleted { get; } = [];

    public IReadOnlyList<string> Reads => Read;

    public string? LastKey { get; private set; }

    public Task<FileWrite> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("存储写入失败（测试注入）。");
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var key = $"key-{Interlocked.Increment(ref _nextKey)}";
        _blobs[key] = buffer.ToArray();
        Written.Add(key);
        LastKey = key;

        return Task.FromResult(new FileWrite(key, Stream.Null));
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        Read.Add(storageKey);

        if (FailReads || !_blobs.TryGetValue(storageKey, out var bytes))
        {
            throw new FileNotFoundException("存储里没有这份字节（测试注入）。", storageKey);
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (FailDeletes)
        {
            throw new IOException("存储删除失败（测试注入）。");
        }

        _blobs.TryRemove(storageKey, out _);
        Deleted.Add(storageKey);
        return Task.CompletedTask;
    }

    public async Task CollectOrphansAsync(Func<string, CancellationToken, Task<bool>> retireUnreferenced,
        DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        foreach (var key in _blobs.Keys.Take(limit))
        {
            if (await retireUnreferenced(key, cancellationToken)) { await DeleteAsync(key, cancellationToken); }
        }
    }
}

/// <summary>不可定位的流：<c>CanSeek</c> 为 <c>false</c>。</summary>
internal sealed class NonSeekableStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
