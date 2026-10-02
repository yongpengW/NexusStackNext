using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Infrastructure;

/// <summary>
/// 本地磁盘字节存储。
///
/// <para><b>它刻意不实现 <c>IFileUrlProvider</c>。</b>本地磁盘给不出一个有期限的可访问链接，
/// 所以那个接口不在它身上——这正是"做不到的能力不给接口"的意思
/// （见 <c>docs/adr/0001-store-and-url-provider-are-separate.md</c>）。
/// 调用方要 URL 就必须显式依赖 <c>IFileUrlProvider</c>，因此不会误以为磁盘存储能给。</para>
///
/// <para><b>存储句柄到路径的解析是一处真实的攻击面。</b>句柄由本类生成（无所谓不可信），
/// 但一旦句柄来自外部（迁移脚本、数据库被改、管理接口），
/// <c>../../</c> 这类值就会让读写落到存储根目录之外。因此解析时**两道闸**：
/// 先拒绝含分隔符与 <c>..</c> 的句柄，再断言拼出来的绝对路径确实在根目录之内。</para>
/// </summary>
public sealed class LocalDiskFileStore : IFileStore, IOrphanFileStore, IDisposable
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private volatile bool _rootCreated;
    private readonly object _initialization = new();
    private string? _storeId;
    private const string IdentityFile = ".nsn-storage-id";
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private IEnumerator<string>? _scan;

    /// <summary>
    /// 构造磁盘存储。**不做任何 I/O**——目录创建推迟到 <see cref="EnsureCreated"/>。
    /// </summary>
    /// <param name="rootDirectory">存储根目录。</param>
    /// <exception cref="ArgumentException">根目录为空。</exception>
    public LocalDiskFileStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _root = Path.GetFullPath(rootDirectory);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        // **构造期不建目录。**
        //
        // 在这里做 I/O 会让"解析这个服务"变成一个可能抛异常的动作——而它发生在容器构建期，
        // 排查时看到的是一句 `Unhandled exception`，看不出是**哪个配置键**写错了。
        // 目录改在显式的启动步骤里建（FileStoreInitializer），那里知道键叫什么，
        // 于是那条错误能直接说"检查 Files:StorageRoot"。
        //
        // 快速失败本身**保留**：路径不可用时进程仍然起不来，只是现在它带着原因起不来。
    }

    /// <summary>
    /// 确保存储根目录存在。**幂等**——建好之后不再碰文件系统。
    /// </summary>
    /// <exception cref="IOException">目录无法创建。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    public void EnsureCreated()
    {
        if (_rootCreated)
        {
            return;
        }

        lock (_initialization)
        {
            if (_rootCreated) { return; }
            Directory.CreateDirectory(_root);
            var identityPath = Path.Combine(_root, IdentityFile);
            if (!File.Exists(identityPath))
            {
                var identity = Guid.NewGuid().ToString("N");
                var pending = identityPath + "." + identity;
                try
                {
                    using (var created = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created.Write(System.Text.Encoding.ASCII.GetBytes(identity));
                        created.Flush(flushToDisk: true);
                    }
                    try { File.Move(pending, identityPath); }
                    catch (IOException) when (File.Exists(identityPath)) { }
                }
                finally { File.Delete(pending); }
            }
            _storeId = ReadStoreIdentity();
            _rootCreated = true;
        }
    }

    /// <summary>存储根目录（绝对路径）。</summary>
    public string RootDirectory => _root;

    /// <inheritdoc />
    public async Task<FileWrite> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // 启动步骤没跑过时（直接构造本类的测试、或是被跳过初始化的宿主），在这里补上。
        EnsureCreated();
        ValidateRoot();

        // 句柄与文件名无关：文件名是不可信输入，而句柄是我们自己生成的。
        var storageKey = "v1-" + _storeId + "-" + Guid.NewGuid().ToString("n");
        var path = ResolvePath(storageKey);
        var protection = new FileStream(path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var partial = path + ".part";
        try
        {
            await using (var target = File.Create(partial))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            File.Move(partial, path);
            return new FileWrite(storageKey, protection);
        }
        catch
        {
            var cleaned = false;
            try { File.Delete(partial); cleaned = true; }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            await protection.DisposeAsync().ConfigureAwait(false);
            if (cleaned)
            {
                try { File.Delete(path + ".lock"); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        ValidateRoot(storageKey);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);

        // File.Exists 会把“目录不可用 / 无权限”也折叠成 false，不能据此宣布清除成功。
        ValidateRoot(storageKey);
        using (var protection = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            DeleteBytes(path);
        }
        File.Delete(path + ".lock");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task CollectOrphansAsync(Func<string, CancellationToken, Task<bool>> retireUnreferenced,
        DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retireUnreferenced);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var storageFailure = false;
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            _scan ??= Directory.EnumerateFiles(_root, "v1-*.lock").GetEnumerator();
            for (var index = 0; index < limit; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_scan.MoveNext()) { _scan.Dispose(); _scan = null; break; }
                var lockPath = _scan.Current;
                var key = Path.GetFileName(lockPath)[..^5];
                if (!IsManagedKey(key) || File.GetCreationTimeUtc(lockPath) >= olderThan.UtcDateTime) { continue; }
                FileStream protection;
                try { protection = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { continue; } // 正在写入，或已被另一个恢复者清除。
                try
                {
                    var retired = false;
                    await using (protection.ConfigureAwait(false))
                    {
                        retired = await retireUnreferenced(key, cancellationToken).ConfigureAwait(false);
                        if (retired) { DeleteBytes(ResolvePath(key)); }
                    }
                    if (retired) { File.Delete(lockPath); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // 保留扫描位置，让单个坏文件不能饿死后续候选；本轮结束后统一报告故障。
                    storageFailure = true;
                }
            }
        }
        catch
        {
            _scan?.Dispose();
            _scan = null;
            throw;
        }
        finally { _scanGate.Release(); }
        if (storageFailure) { throw new IOException("部分孤儿文件暂时无法清理，将继续重试。"); }
    }

    private void ValidateRoot(string? storageKey = null)
    {
        if (!string.Equals(_storeId, ReadStoreIdentity(), StringComparison.Ordinal)
            || (storageKey is not null && !IsManagedKey(storageKey)))
        {
            throw new IOException("文件存储身份不匹配，请恢复原存储。");
        }
    }

    private string ReadStoreIdentity()
    {
        var identity = File.ReadAllText(Path.Combine(_root, IdentityFile));
        if (!Guid.TryParseExact(identity, "N", out _)) { throw new IOException("文件存储身份无效。"); }
        return identity;
    }

    private static void DeleteBytes(string path)
    {
        File.Delete(path);
        File.Delete(path + ".part");
    }

    private bool IsManagedKey(string storageKey) => storageKey.StartsWith("v1-" + _storeId + "-", StringComparison.Ordinal)
        && Guid.TryParseExact(storageKey.AsSpan(36), "N", out _);

    /// <inheritdoc />
    public void Dispose()
    {
        _scan?.Dispose();
        _scanGate.Dispose();
    }

    /// <summary>
    /// 把存储句柄解析成绝对路径。
    /// <para>两道闸：句柄本身不许含分隔符或 <c>..</c>；解析结果必须仍在根目录之内。</para>
    /// </summary>
    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)
            || storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.Contains('/', StringComparison.Ordinal)
            || storageKey.Contains('\\', StringComparison.Ordinal)
            || storageKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"存储句柄不合法：{storageKey}。");
        }

        var full = Path.GetFullPath(Path.Combine(_root, storageKey));

        return full.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new InvalidOperationException($"存储句柄逃出了存储根目录：{storageKey}。");
    }
}

/// <summary>仅用于开发测试的文件元数据仓储；查询返回快照，提交时比较版本。</summary>
public sealed class InMemoryStoredFileRepository : IStoredFileRepository
{
    private readonly Dictionary<long, StoredFile> _files = [];
    private readonly object _gate = new();
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            return Task.FromResult(
                _files.TryGetValue(id.Value, out var file) && !file.IsDeleted ? file.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    public Task<StoredFile?> FindDeletedAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return Task.FromResult(_files.TryGetValue(id.Value, out var file) && file.IsDeleted ? file.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredFile>> PendingDeletionsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredFile>>(_files.Values
                .Where(file => file.IsDeleted && file.BytesRemovedAt is null
                    && (file.NextCleanupAttemptAt is null || file.NextCleanupAttemptAt <= now))
                .OrderBy(file => file.NextCleanupAttemptAt).ThenBy(file => file.Id.Value).Take(limit).Select(file => file.Snapshot()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(StoredFile file, long? originalVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        lock (_gate)
        {
            if (file.StorageKey is not null && _retired.Contains(file.StorageKey))
            {
                throw new InvalidOperationException("该文件写入已失效，不能发布元数据。");
            }
            var exists = _files.TryGetValue(file.Id.Value, out var current);
            if (originalVersion is null ? exists : !exists || current!.Version != originalVersion.Value)
            {
                throw new FileMetadataConflictException();
            }
            _files[file.Id.Value] = file.Snapshot();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RetireUnreferencedStorageAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_files.Values.Any(file => string.Equals(file.StorageKey, storageKey, StringComparison.Ordinal))) { return Task.FromResult(false); }
            _retired.Add(storageKey);
            return Task.FromResult(true);
        }
    }
}

/// <summary>把 Files 的端口接到适配器上。</summary>
public static class FilesInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册本地磁盘字节存储；元数据适配器由模块独立选择。<b>不做程序集扫描</b>（架构不变量 8）。
    ///
    /// <para><b>注册期不 new、也不碰文件系统。</b>存储用工厂注册，
    /// <c>LocalDiskFileStore</c> 的构造期已不再做 I/O（见该类的说明）。
    /// 目录创建是一个**显式的启动步骤**，由模块登记——因为"这个路径来自哪个配置键"
    /// 是模块的知识，错误信息要说出它就得在那里组装。</para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="storageRoot">存储根目录。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddFilesLocalDiskStorage(this IServiceCollection services, string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);

        services.AddSingleton(_ => new LocalDiskFileStore(storageRoot));

        // 同一个实例同时以具体类型与端口暴露：启动步骤要具体类型上的 EnsureCreated，
        // 而业务代码只看得到 IFileStore。
        services.AddSingleton<IFileStore>(sp => sp.GetRequiredService<LocalDiskFileStore>());
        services.AddSingleton<IOrphanFileStore>(sp => sp.GetRequiredService<LocalDiskFileStore>());

        services.AddScoped<FileService>();
        services.AddScoped<FileRecovery>();
        services.AddHostedService<FileRecoveryWorker>();

        return services;
    }
}
