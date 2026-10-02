using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;

namespace NexusStackNext.Files.Endpoints;

/// <summary>
/// Files 模块：本上下文对宿主暴露的全部内容——DI 注册、健康检查与 HTTP 端点。
///
/// <para>存储能力由两个**窄端口**描述（<c>IFileStore</c> / <c>IFileUrlProvider</c>），
/// 而不是参照仓库那个 14 个成员、没有任何实现能全部满足的 <c>IFileStorage</c>。</para>
///
/// <para>当前字节存储是**本地磁盘**实现。它刻意不实现 <c>IFileUrlProvider</c>——
/// 磁盘给不出有期限的链接，那个能力属于对象存储（见 docs/adr/0001）。</para>
///
/// <para>模块边界见 <c>NexusStackNext.Auditing.Endpoints.AuditingModule</c> 的说明（ADR-0013）。</para>
/// </summary>
public static class FilesModule
{
    /// <summary>存储根目录来自哪个配置键。**只在这里写一次**——启动步骤的错误信息里也要用它。</summary>
    public const string StorageRootConfigurationKey = "Files:StorageRoot";

    /// <summary>注册本模块需要的服务，包含它自己的就绪检查。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置——本模块自己知道要读哪个键。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddFilesModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var storageRoot = configuration[StorageRootConfigurationKey];
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = Path.Combine(AppContext.BaseDirectory, "file-storage");
        }

        // 键名**在模块里**用：配置键属于模块，而"路径不可用"的错误要说得出它是谁。
        services.AddFilesLocalDiskStorage(storageRoot);

        // 目录创建是一个**显式的启动步骤**，不是注册期的副作用。
        // 路径不可用时进程仍然起不来（这份快速失败是有意保留的），
        // 但现在它带着一条能直接照着修的错误：路径是什么、去改哪个键。
        services.AddHostedService(sp => new FileStoreInitializer(
            sp.GetRequiredService<LocalDiskFileStore>(),
            StorageRootConfigurationKey,
            sp.GetRequiredService<ILogger<FileStoreInitializer>>()));

        // **Files 有真实依赖可查**：存储写不进去时，它此前照样报"健康"。
        // 检查由模块自己登记——宿主不该知道 Files 需要查什么。
        services.AddHealthChecks().AddCheck<FileStorageHealthCheck>("storage");

        return services;
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // **进程内也要要求认证**：每个端点末尾的 `.RequireAuthorization()`。
        //
        // 部署不变量说"业务服务不对外暴露、边缘是唯一入口"，而那条约定**没有任何测试守着**。
        // 网关确实会挡（`files-api` 是 requireAuthentication: true），但那是**编排**的事实，
        // 不是代码的事实：谁能直连到这个进程，谁就绕过了整套保护。
        // 此前这五个端点在进程内**没有任何授权判定**——宿主注释里那句"授权过滤器由模块
        // 自己挂在它的分组上"只对 Identity 成立。
        //
        // 为什么用框架的 `RequireAuthorization()` 而不是 Identity 那个过滤器：
        // 那个要算**权限键**（路由模板:方法）并比对预计算集合，是 RBAC 的落点；
        // 这四个上下文还没有登记权限键，它们要的只是"令牌有效"——那正是框架能力的范围。
        // 等哪个上下文开始登记权限键，再把它换成过滤器。

        // 上传。**请求体就是文件字节**，文件名走查询串。
        // 为什么不用 multipart：那一层是传输细节，而这里要验证的是领域与存储的接线。
        // 需要 multipart 时它可以在这一层之上加，不影响下面任何东西。
        endpoints.MapPost("/api/files", async (ApiResponses responses,
            HttpRequest request,
            string name,
            FileService files,
            CancellationToken cancellationToken) =>
        {
            var fileName = FileName.Create(name);
            if (fileName.IsFailure)
            {
                return Failure(fileName.Error);
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            var uploaded = await files.UploadAsync(
                fileName.Value,
                request.ContentType ?? "application/octet-stream",
                buffer,
                ownerId: null,
                cancellationToken);

            return uploaded.IsFailure
                ? Failure(uploaded.Error)
                : responses.Created($"/api/files/{uploaded.Value.Id.Value}", new FileUploadedResponse(uploaded.Value.Id.Value, uploaded.Value.Name.Value, uploaded.Value.Size, uploaded.Value.StorageKey));
        }).Produces<ApiResponse<FileUploadedResponse>>(201).ProducesApiErrors(400, 401, 403, 500).RequireAuthorization();

        // 下载字节。**文件名由领域校验过**，因此这里不必再防路径穿越——
        // 而磁盘存储解析句柄时还有第二道闸（见 LocalDiskFileStore）。
        endpoints.MapGet("/api/files/{id:long}", async (
            long id,
            FileService files,
            CancellationToken cancellationToken) =>
        {
            var opened = await files.OpenAsync(new StoredFileId(id), cancellationToken);

            return opened.IsFailure
                ? Failure(opened.Error)
                : Results.File(opened.Value.Content, opened.Value.File.ContentType, opened.Value.File.Name.Value);
        }).Produces(200, contentType: "application/octet-stream").ProducesApiErrors(400, 401, 403, 404, 500).RequireAuthorization();

        // 元数据（不碰字节）。
        endpoints.MapGet("/api/files/{id:long}/metadata", async (ApiResponses responses,
            long id,
            FileService files,
            CancellationToken cancellationToken) =>
        {
            var file = await files.DescribeAsync(new StoredFileId(id), cancellationToken);

            return file is null
                ? Failure(new Error("files.not_found", $"文件不存在：{id}。"))
                : responses.Ok(new FileMetadataResponse(file.Id.Value, file.Name.Value, file.ContentType, file.Size, file.IsStored, file.UploadedAt));
        }).Produces<ApiResponse<FileMetadataResponse>>().ProducesApiErrors(400, 401, 403, 404, 500).RequireAuthorization();

        // 删除：先软删元数据，再删字节（顺序的理由见 FileService）。
        endpoints.MapDelete("/api/files/{id:long}", async (
            long id,
            FileService files,
            CancellationToken cancellationToken) =>
        {
            var deleted = await files.DeleteAsync(new StoredFileId(id), cancellationToken);

            return deleted.IsFailure ? Failure(deleted.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(400, 401, 403, 404, 500).RequireAuthorization();

        // 校验文件名——目录穿越的第一道闸在领域里，这里只是把它暴露出来。
        endpoints.MapGet("/api/files/validate-name", (ApiResponses responses, string name) =>
        {
            var parsed = FileName.Create(name);

            return parsed.IsFailure
                ? Failure(parsed.Error)
                : responses.Ok(new FileNameResponse(parsed.Value.Value));
        }).Produces<ApiResponse<FileNameResponse>>().ProducesApiErrors(400, 401, 403, 500).RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para>与 Platform 的**故意不同**：这里 <c>files.not_found</c> 是 404，
    /// 而 Platform 的全部是 400。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code == "files.not_found"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>
/// 在**启动时**把存储根目录准备好。
///
/// <para><b>为什么不放在注册期。</b>目录创建原先发生在
/// <c>AddFilesLocalDiskStorage</c> 里那次 <c>new LocalDiskFileStore(...)</c> 上。
/// 注册服务是**纯内存动作**，让它可以碰文件系统就等于让"组装这个应用"可能抛异常——
/// 而那已经脱离了"我在配置哪个服务"的思路，排查时看到的只是一句
/// <c>Unhandled exception</c>，看不出是哪个配置键写错了。</para>
///
/// <para><b>快速失败本身保留。</b>路径不可用时进程仍然起不来——这是 AGENTS.md 里
/// 记着的有意行为（与"运行期存储掉线 → 就绪检查报 503"互补）。
/// 变的是它现在**说得出原因**：路径是什么、去改哪个键。</para>
/// </summary>
/// <param name="store">磁盘存储。</param>
/// <param name="configurationKey">存储根目录来自哪个配置键。</param>
/// <param name="logger">日志。</param>
internal sealed partial class FileStoreInitializer(
    LocalDiskFileStore store,
    string configurationKey,
    ILogger<FileStoreInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            store.EnsureCreated();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 一条**能直接照着修**的错误：路径是什么、去改哪个键。
            throw new InvalidOperationException(
                $"文件存储根目录不可用：'{store.RootDirectory}'。请检查配置键 {configurationKey}。",
                exception);
        }

        LogStorageReady(logger, store.RootDirectory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "文件存储根目录就绪：{RootDirectory}")]
    private static partial void LogStorageReady(ILogger logger, string rootDirectory);
}

internal sealed record FileUploadedResponse(long FileId, string Name, long Size, string? StorageKey);
internal sealed record FileMetadataResponse(long FileId, string Name, string ContentType, long Size, bool Stored, DateTimeOffset UploadedAt);
internal sealed record FileNameResponse(string FileName);
