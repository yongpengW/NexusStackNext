using System.Text.Json.Serialization;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Exports;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>本人调查导出的受权 HTTP 接口。</summary>
public static class AuditExportsModule
{
    /// <summary>显式装配证书协议、启动配置校验和单份后台处理。</summary>
    /// <param name="services">宿主容器。</param>
    /// <param name="configuration">独立审计导出配置。</param>
    /// <param name="environment">宿主环境。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddAuditExportDelivery(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var transport = configuration.GetSection("Auditing:Exports:Files").Get<GeneratedExportFilesOptions>() ?? new();
        services.AddSingleton<IExportFiles>(_ => new GeneratedExportFilesClient(transport, AuditExportAccessV1.Producer, environment.EnvironmentName));
        services.AddHostedService<AuditExportStartup>();
        if (configuration.GetValue("Auditing:Exports:Worker:Enabled", true)) { services.AddHostedService<AuditExportWorker>(); }
        return services;
    }
    /// <summary>映射申请、本人中心与取消；各入口使用独立于查询的同一导出许可。</summary>
    /// <param name="endpoints">当前宿主路由。</param>
    public static void MapAuditExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/auditing/exports").RequireAuthorization();
        group.AddEndpointFilter<NexusStackAuthorizationFilter>();
        group.MapPost("/", async (HttpRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var parsed = await AuditExportJson.ReadAsync(request, token).ConfigureAwait(false);
            if (parsed.IsFailure) { return Failure(parsed.Error); }
            var result = await sender.SendAsync(parsed.Value, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationDescription("auditing.export.accept", "接受私有调查导出"))
            .Accepts<AcceptAuditExport>("application/json")
            .Produces<ApiResponse<AuditExportStatus>>(202).ProducesApiErrors(400, 401, 403, 408, 409, 413, 415, 503);
        group.MapGet("/{exportId:guid}", async (Guid exportId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetAuditExport(exportId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationLogSuppression("本人导出状态轮询不生成新的调查操作。"))
            .Produces<ApiResponse<AuditExportStatus>>();
        group.MapGet("/", async (int? page, int? limit, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new ListAuditExports(page ?? 1, limit ?? 50), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationLogSuppression("本人导出中心读取不生成新的调查操作。"))
            .Produces<ApiResponse<IReadOnlyList<AuditExportStatus>>>();
        group.MapPost("/{exportId:guid}/cancel", async (Guid exportId, AuditExportVersion request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new CancelAuditExport(exportId, request.ExpectedVersion), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationDescription("auditing.export.cancel", "取消本人调查导出", new OperationSubjectRoute("AuditExport", "exportId", OperationSubjectIdKind.Uuid)));
        group.MapPost("/{exportId:guid}/retry", async (Guid exportId, AuditExportVersion request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryAuditExport(exportId, request.ExpectedVersion), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationDescription("auditing.export.retry", "恢复本人原调查导出", new OperationSubjectRoute("AuditExport", "exportId", OperationSubjectIdKind.Uuid)));
        group.MapGet("/{exportId:guid}/artifact", async (Guid exportId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetAuditExportArtifact(exportId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission(AuditExportAccessV1.PermissionRoute, AuditExportAccessV1.PermissionMethod)
            .WithMetadata(new OperationLogSuppression("本人导出可用性轮询不生成新的调查操作。"))
            .Produces<ApiResponse<AuditExportArtifact>>();
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message,
        statusCode: error.Code switch
        {
            "auditing.export.not_found" => 404,
            "auditing.export.unauthenticated" => 401,
            "auditing.export.request_conflict" or "auditing.export.cancel_conflict" or "auditing.export.retry_conflict" or "auditing.export.not_ready" => 409,
            "auditing.export.unavailable" or "auditing.export.files_unavailable" => 503,
            _ => 400,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AuditExportVersion([property: JsonRequired] long ExpectedVersion);

internal sealed class AuditExportStartup(IExportFiles files) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(files); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
