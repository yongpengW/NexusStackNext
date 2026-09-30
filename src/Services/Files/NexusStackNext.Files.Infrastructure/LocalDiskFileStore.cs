using System.Collections.Concurrent;
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
public sealed class LocalDiskFileStore : IFileStore
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private volatile bool _rootCreated;

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

        Directory.CreateDirectory(_root);
        _rootCreated = true;
    }

    /// <summary>存储根目录（绝对路径）。</summary>
    public string RootDirectory => _root;

    /// <inheritdoc />
    public async Task<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // 启动步骤没跑过时（直接构造本类的测试、或是被跳过初始化的宿主），在这里补上。
        EnsureCreated();

        // 句柄与文件名无关：文件名是不可信输入，而句柄是我们自己生成的。
        var storageKey = Guid.NewGuid().ToString("n");
        var path = ResolvePath(storageKey);

        await using (var target = File.Create(path))
        {
            await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return storageKey;
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);

        return File.Exists(path)
            ? Task.FromResult<Stream>(File.OpenRead(path))
            : throw new FileNotFoundException($"存储中没有这个句柄：{storageKey}。", storageKey);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
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

/// <summary>内存文件元数据仓储。<b>只保存未软删的文件</b>——调用方不需要自己记得过滤。</summary>
public sealed class InMemoryStoredFileRepository : IStoredFileRepository
{
    private readonly ConcurrentDictionary<long, StoredFile> _files = new();

    /// <inheritdoc />
    public Task<StoredFile?> FindAsync(StoredFileId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return Task.FromResult(
            _files.TryGetValue(id.Value, out var file) && !file.IsDeleted ? file : null);
    }

    /// <inheritdoc />
    public Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        _files[file.Id.Value] = file;
        return Task.CompletedTask;
    }
}

/// <summary>把 Files 的端口接到适配器上。</summary>
public static class FilesInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册本地磁盘存储与内存元数据仓储。<b>显式注册，不做程序集扫描</b>（架构不变量 8）。
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

        services.AddSingleton<IStoredFileRepository, InMemoryStoredFileRepository>();
        services.AddScoped<FileService>();

        return services;
    }
}
