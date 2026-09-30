using System.Collections.Concurrent;
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
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static (FileService Service, FakeFileStore Store, FakeStoredFileRepository Files) NewService()
    {
        var store = new FakeFileStore();
        var files = new FakeStoredFileRepository();
        return (new FileService(store, files, new SequentialIdGenerator(7000), new FixedClock(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero))), store, files);
    }

    private static MemoryStream Content(string text = "hello") => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Upload_WritesBytesThenSavesMetadata()
    {
        var (service, store, files) = NewService();

        var result = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content("hello"));

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value.Size);
        Assert.Single(store.Written);
        Assert.Single(files.Saved);
        Assert.Equal(result.Value.StorageKey, Assert.Single(files.Saved).StorageKey);
    }

    [Fact]
    public async Task Upload_WhenTheStoreFails_LeavesNoMetadataBehind()
    {
        // 这是"先写字节再标记"这个顺序要防的那件事：
        // 反过来会留下"元数据说已存储、字节根本不在"，而它只会在**下载**时才以 500 暴露。
        var (service, store, files) = NewService();
        store.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(
            () => service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content()));

        Assert.Empty(files.Saved);
    }

    [Fact]
    public async Task Upload_RejectsANonSeekableStream()
    {
        // "内容流必须可定位"是写在接口文档上的约束，因此它必须有测试盯着——
        // 否则约束会退化成"运行时才发现"。
        var (service, _, _) = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", new NonSeekableStream()));
    }

    [Fact]
    public async Task Upload_RejectsEmptyContentType()
    {
        var (service, store, files) = NewService();

        var result = await service.UploadAsync(FileName.Create("a.txt").Value, "  ", Content());

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_type.empty", result.Error.Code);
        Assert.Empty(store.Written);
        Assert.Empty(files.Saved);
    }

    [Fact]
    public async Task Open_UnknownFile_Fails()
    {
        var (service, _, _) = NewService();

        var result = await service.OpenAsync(new StoredFileId(404));

        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Open_RegisteredButNeverStored_FailsWithContentMissing()
    {
        var (service, _, files) = NewService();
        var file = StoredFile.Register(new StoredFileId(1), FileName.Create("a.txt").Value, "text/plain", null, Now).Value;
        await files.SaveAsync(file);

        var result = await service.OpenAsync(new StoredFileId(1));

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_missing", result.Error.Code);
    }

    [Fact]
    public async Task Open_WhenTheStoreCannotFindTheBytes_FailsInsteadOfThrowing()
    {
        // 元数据在、字节不在——这是数据损坏或迁移出错后的状态。
        // 它必须以一个**明确的失败**返回，而不是把存储的异常直接抛给调用方：
        // 调用方拿到 Result 才能决定是 404、是 500、还是告警。
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content());
        store.FailReads = true;

        var result = await service.OpenAsync(uploaded.Value.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_missing", result.Error.Code);
    }

    [Fact]
    public async Task Open_SoftDeletedFile_LooksLikeItDoesNotExist()
    {
        var (service, _, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content());

        await service.DeleteAsync(uploaded.Value.Id);

        var result = await service.OpenAsync(uploaded.Value.Id);
        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Delete_SoftDeletesMetadataThenRemovesBytes()
    {
        var (service, store, files) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content());

        var result = await service.DeleteAsync(uploaded.Value.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(store.Deleted);
        Assert.True(Assert.Single(files.Saved).IsDeleted);
    }

    [Fact]
    public async Task Delete_WhenBytesCannotBeRemoved_ReportsItInsteadOfPretendingSuccess()
    {
        // 元数据已经软删了，字节删不掉——文件对用户已经不可见，但字节还在。
        // **返回成功会让这件事永远没人知道。** 所以给一个可区分的失败码，
        // 让调用方能告警或重试，而不是拿到 204 以为干净了。
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content());
        store.FailDeletes = true;

        var result = await service.DeleteAsync(uploaded.Value.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("files.bytes_not_removed", result.Error.Code);
    }

    [Fact]
    public async Task Delete_UnknownFile_Fails()
    {
        var (service, _, _) = NewService();

        var result = await service.DeleteAsync(new StoredFileId(404));

        Assert.True(result.IsFailure);
        Assert.Equal("files.not_found", result.Error.Code);
    }

    [Fact]
    public async Task Describe_ReturnsMetadataWithoutTouchingBytes()
    {
        var (service, store, _) = NewService();
        var uploaded = await service.UploadAsync(FileName.Create("a.txt").Value, "text/plain", Content());

        var described = await service.DescribeAsync(uploaded.Value.Id);

        Assert.NotNull(described);
        Assert.Equal("a.txt", described!.Name.Value);
        Assert.Empty(store.Reads);
    }
}

/// <summary>可配置失败的存储替身。用它把"磁盘坏掉"变成可测的输入。</summary>
internal sealed class FakeFileStore : IFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public bool FailWrites { get; set; }

    public bool FailReads { get; set; }

    public bool FailDeletes { get; set; }

    public List<string> Written { get; } = [];

    public List<string> Read { get; } = [];

    public List<string> Deleted { get; } = [];

    public IReadOnlyList<string> Reads => Read;

    public string? LastKey { get; private set; }

    public Task<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("存储写入失败（测试注入）。");
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var key = $"key-{_blobs.Count}";
        _blobs[key] = buffer.ToArray();
        Written.Add(key);
        LastKey = key;

        return Task.FromResult(key);
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
}

/// <summary>元数据仓储替身。<b>查找时过滤已软删</b>——与端口契约一致。</summary>
internal sealed class FakeStoredFileRepository : IStoredFileRepository
{
    private readonly ConcurrentDictionary<long, StoredFile> _files = new();

    public IReadOnlyList<StoredFile> Saved => [.. _files.Values];

    public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(id.Value, out var file) && !file.IsDeleted ? file : null);

    public Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        _files[file.Id.Value] = file;
        return Task.CompletedTask;
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
