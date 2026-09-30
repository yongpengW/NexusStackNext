using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Domain.Tests;

/// <summary>
/// 验证"文件存储"这道缝上**确实有东西在变化**。
/// <para>
/// `codebase-design` 的判据：<b>一个适配器只是假设的缝，两个才是真的缝。</b>
/// 这道缝上具名的一对是"本地磁盘"与"对象存储"——差别不在于实现细节，
/// 而在于**对象存储能签发访问链接、本地磁盘不能**。
/// </para>
/// <para>
/// 因此 `IFileStore` 只有三个方法，"给个 URL" 这件事在另一个接口
/// （<see cref="IFileUrlProvider"/>）上。本地磁盘存储**不必**实现它，
/// 也就不会出现"声明了却抛 <c>NotImplementedException</c>"的中间状态。
/// </para>
/// <para>
/// 这里用两个语义刻意不同的适配器跑**同一组契约断言**，检验接口是否需要为其中任何一个弯曲。
/// 裂缝说明设计错了；两边都能满足而接口不动，才叫验证过。
/// </para>
/// </summary>
public sealed class FileStoreSeamTests
{
    [Fact]
    public async Task BothAdapters_SatisfyTheSameReadWriteContract()
    {
        await AssertStoreContract(new LocalDiskStore());
        await AssertStoreContract(new ObjectStore());
    }

    [Fact]
    public async Task BothAdapters_FailTheSameWayOnAnUnknownKey()
    {
        // 错误模式是接口的一部分，因此两个适配器必须表现一致。
        var local = new LocalDiskStore();
        var remote = new ObjectStore();

        await Assert.ThrowsAnyAsync<Exception>(() => local.OpenReadAsync("does-not-exist"));
        await Assert.ThrowsAnyAsync<Exception>(() => remote.OpenReadAsync("remote/does-not-exist"));
    }

    [Fact]
    public void OnlyTheObjectStore_ClaimsTheUrlCapability()
    {
        // 这道缝存在的**唯一理由**。如果两者都实现了 IFileUrlProvider，
        // 那就该合成一个接口；如果都不实现，这个接口就是多余的。
        //
        // 注意这里先赋给 IFileStore 再判类型：直接在具体类型上写 `is`，
        // 编译器会算出它是常量并报 CS0184——那本身就是"两者毫无关系"的证明。
        IFileStore local = new LocalDiskStore();
        IFileStore remote = new ObjectStore();

        Assert.False(local is IFileUrlProvider);
        Assert.True(remote is IFileUrlProvider);
    }

    [Fact]
    public async Task UrlProvider_IsUsableWithoutTouchingTheStoreInterface()
    {
        var store = new ObjectStore();
        var key = await store.WriteAsync(new MemoryStream([1, 2, 3]), "application/octet-stream");

        var url = await store.GetReadUrlAsync(key, TimeSpan.FromMinutes(5));

        Assert.StartsWith("https://", url.ToString(), StringComparison.Ordinal);
        Assert.Contains(key, url.ToString(), StringComparison.Ordinal);
    }

    private static async Task AssertStoreContract(IFileStore store)
    {
        var payload = new byte[] { 9, 8, 7 };

        var key = await store.WriteAsync(new MemoryStream(payload), "application/octet-stream");
        Assert.False(string.IsNullOrWhiteSpace(key));

        await using (var read = await store.OpenReadAsync(key))
        {
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);

            Assert.Equal(payload, buffer.ToArray());
        }

        await store.DeleteAsync(key);

        await Assert.ThrowsAnyAsync<Exception>(() => store.OpenReadAsync(key));
    }

    /// <summary>适配器 A：本地磁盘语义。有路径、没有 URL 能力。</summary>
    private sealed class LocalDiskStore : IFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Task<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);

            var key = $"local/{_files.Count}";
            _files[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            _files.TryGetValue(storageKey, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException($"本地磁盘上没有 {storageKey}。", storageKey);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            _files.Remove(storageKey);
            return Task.CompletedTask;
        }
    }

    /// <summary>适配器 B：对象存储语义。键带前缀、能签发有期限的链接。</summary>
    private sealed class ObjectStore : IFileStore, IFileUrlProvider
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);

            var key = $"remote/{Guid.NewGuid():n}";
            _objects[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            _objects.TryGetValue(storageKey, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException($"对象存储上没有 {storageKey}。", storageKey);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            _objects.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<Uri> GetReadUrlAsync(string storageKey, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri($"https://objects.example/{storageKey}?expires={lifetime.TotalSeconds:F0}"));
    }
}
