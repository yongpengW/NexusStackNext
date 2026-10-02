using Microsoft.AspNetCore.Http.Features;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Security;
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
    /// <param name="environment">运行环境；内存元数据仅用于开发和测试。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddFilesModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddSingleton(new FileUploadLimits(
            configuration.GetValue<long?>("Files:Upload:MaxBytes") ?? 64 * 1024 * 1024,
            configuration.GetValue<int?>("Files:Upload:MaxConcurrentUploads") ?? 4));
        services.AddSingleton(new FileRecoveryOptions(
            configuration.GetValue<int?>("Files:Cleanup:IntervalSeconds") ?? 30,
            configuration.GetValue<int?>("Files:Cleanup:BatchSize") ?? 64,
            configuration.GetValue<int?>("Files:Cleanup:RetryDelaySeconds") ?? 30,
            configuration.GetValue<int?>("Files:Cleanup:OrphanAgeSeconds") ?? 3600));

        var provider = configuration["Files:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) { provider = "Postgres"; }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Files:Storage:Provider=Memory 仅允许 Development / Testing 环境。");
            }
            services.AddSingleton<IStoredFileRepository, InMemoryStoredFileRepository>();
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("Files");
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("必须配置 ConnectionStrings:Files；开发测试可显式选择 Files:Storage:Provider=Memory。");
            }
            services.AddFilesPostgresMetadata(connection);
        }
        else
        {
            throw new InvalidOperationException("Files:Storage:Provider 仅支持 Postgres / Memory。");
        }

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

        // 会话撤销检查先于归属判断；根管理员也不隐式拥有其他人的私有文件。
        var fileEndpoints = endpoints.MapGroup("/api/files").RequireAuthorization().RequireAuthenticated();
        fileEndpoints.AddEndpointFilter<NexusStackAuthorizationFilter>();
        // 上传。**请求体就是文件字节**，文件名走查询串。
        // 为什么不用 multipart：那一层是传输细节，而这里要验证的是领域与存储的接线。
        // 需要 multipart 时它可以在这一层之上加，不影响下面任何东西。
        fileEndpoints.MapPost("", async (ApiResponses responses,
            HttpRequest request,
            string name,
            FileService files,
            FileUploadLimits limits,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var fileName = FileName.Create(name);
            if (fileName.IsFailure)
            {
                return Failure(fileName.Error);
            }

            if (request.ContentLength > limits.MaxBytes)
            {
                return Failure(new Error("files.too_large", "文件超过上传大小限制。"));
            }
            var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) { bodyLimit.MaxRequestBodySize = limits.MaxBytes; }

            var uploaded = await files.UploadAsync(
                fileName.Value,
                request.ContentType ?? "application/octet-stream",
                request.Body,
                ownerId: currentUser.UserId!,
                cancellationToken);

            return uploaded.IsFailure
                ? Failure(uploaded.Error)
                : responses.Created($"/api/files/{uploaded.Value.Id.Value}", new FileUploadedResponse(uploaded.Value.Id.Value, uploaded.Value.Name.Value, uploaded.Value.Size));
        }).Produces<ApiResponse<FileUploadedResponse>>(201).ProducesApiErrors(400, 401, 403, 413, 429, 500).RequireAuthorization();

        // 下载字节。**文件名由领域校验过**，因此这里不必再防路径穿越——
        // 而磁盘存储解析句柄时还有第二道闸（见 LocalDiskFileStore）。
        fileEndpoints.MapGet("/{id:long}", async (
            long id,
            HttpResponse response,
            FileService files,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var opened = await files.OpenAsync(new StoredFileId(id), currentUser.UserId!, cancellationToken);

            if (opened.IsFailure) { return Failure(opened.Error); }
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(opened.Value.Content, opened.Value.File.ContentType, opened.Value.File.Name.Value);
        }).Produces(200, contentType: "application/octet-stream").ProducesApiErrors(400, 401, 403, 404, 500, 503).RequireAuthorization();

        // 元数据（不碰字节）。
        fileEndpoints.MapGet("/{id:long}/metadata", async (ApiResponses responses,
            long id,
            FileService files,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var file = await files.DescribeAsync(new StoredFileId(id), currentUser.UserId!, cancellationToken);

            return file is null
                ? Failure(new Error("files.not_found", $"文件不存在：{id}。"))
                : responses.Ok(new FileMetadataResponse(file.Id.Value, file.Name.Value, file.ContentType, file.Size, file.IsStored, file.UploadedAt, EntityAuditMetadata.From(file)));
        }).Produces<ApiResponse<FileMetadataResponse>>().ProducesApiErrors(400, 401, 403, 404, 500).RequireAuthorization();

        // 删除：先软删元数据，再删字节（顺序的理由见 FileService）。
        fileEndpoints.MapDelete("/{id:long}", async (
            ApiResponses responses,
            HttpResponse response,
            long id,
            FileService files,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var deleted = await files.DeleteAsync(new StoredFileId(id), currentUser.UserId!, cancellationToken);
            if (deleted.IsSuccess && !deleted.Value) { response.Headers.Location = $"/api/files/{id}/deletion"; }
            return deleted.IsFailure ? Failure(deleted.Error)
                : deleted.Value ? Results.NoContent()
                : responses.Accepted(new FileDeletionResponse(id, false));
        }).Produces(204).Produces<ApiResponse<FileDeletionResponse>>(202).ProducesApiErrors(400, 401, 403, 404, 500).RequireAuthorization();

        fileEndpoints.MapGet("/{id:long}/deletion", async (
            ApiResponses responses,
            long id,
            FileService files,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var completed = await files.DeletionCompletedAsync(new StoredFileId(id), currentUser.UserId!, cancellationToken);
            return completed is null ? Failure(new Error("files.not_found", "文件不存在。"))
                : responses.Ok(new FileDeletionResponse(id, completed.Value));
        }).Produces<ApiResponse<FileDeletionResponse>>().ProducesApiErrors(401, 403, 404, 500);

        // 校验文件名——目录穿越的第一道闸在领域里，这里只是把它暴露出来。
        fileEndpoints.MapGet("/validate-name", (ApiResponses responses, string name) =>
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
    /// <para>文件不存在、资源超限与普通校验错误有各自的 HTTP 语义。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code switch
        {
            "files.not_found" => StatusCodes.Status404NotFound,
            "files.content_missing" => StatusCodes.Status503ServiceUnavailable,
            "files.too_large" => StatusCodes.Status413PayloadTooLarge,
            "files.upload_busy" => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest,
        },
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

internal sealed record FileUploadedResponse(long FileId, string Name, long Size);
internal sealed record FileMetadataResponse(long FileId, string Name, string ContentType, long Size, bool Stored, DateTimeOffset UploadedAt, EntityAuditMetadata? Audit);
internal sealed record FileNameResponse(string FileName);
internal sealed record FileDeletionResponse(long FileId, bool Completed);
