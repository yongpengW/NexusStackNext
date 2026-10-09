using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;

namespace NexusStackNext.Pricing.Endpoints;

/// <summary>定价私有导出的显式宿主装配与本人 HTTP 契约。</summary>
public static class PricingExportsModule
{
    /// <summary>启用专用 HTTPS 适配器、生成与发布处理器。</summary>
    /// <param name="services">宿主服务。</param>
    /// <param name="configuration">私有服务配置。</param>
    /// <param name="environmentName">宿主环境。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddPricingExports(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddPricingExportPersistence(configuration.GetSection("Pricing:Exports:Execution").Get<PricingExportOptions>());
        services.AddPricingExportFiles(configuration.GetSection("Pricing:Exports:Files").Get<PricingExportFilesOptions>() ?? new(), environmentName)
            .AddPricingExportProcessing().AddHostedService<PricingExportStartup>();
        if (configuration.GetValue("Pricing:Exports:Worker:Enabled", true)) { services.AddHostedService<PricingExportWorker>(); }
        return services;
    }

    /// <summary>映射本人导出入口，执行协议永不映射。</summary>
    /// <param name="endpoints">宿主路由。</param>
    /// <returns>原路由。</returns>
    public static IEndpointRouteBuilder MapPricingExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/pricing/exports").RequireAuthorization("pricing-operator")
            .ProducesApiErrors(400, 401, 403, 404, 409, 500, 503);
        group.MapPost("/", async (HttpRequest request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var parsed = await PricingExportJson.ReadAsync(request, token).ConfigureAwait(false);
            if (parsed.IsFailure) { return Failure(parsed.Error); }
            var accepted = await sender.SendAsync(parsed.Value, token).ConfigureAwait(false);
            return accepted.IsSuccess ? (IResult)responses.Accepted(accepted.Value) : Failure(accepted.Error);
        }).RequirePermission("/api/pricing/exports", "POST")
            .Accepts<AcceptPricingExport>("application/json")
            .WithMetadata(new OperationDescription("pricing.export.accept", "接受私有报价 CSV 导出"))
            .Produces<ApiResponse<PricingExportStatus>>(202).ProducesApiErrors(408, 413, 415);
        group.MapGet("/", async (int? limit, string? state, DateTimeOffset? acceptedFrom, DateTimeOffset? acceptedThrough, string? cursor,
            ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new ListPricingExports(limit ?? 50, state, acceptedFrom, acceptedThrough, cursor), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/pricing/exports", "GET").Produces<ApiResponse<PricingExportPage>>();
        group.MapGet("/{exportId:guid}", async (Guid exportId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetPricingExport(exportId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/pricing/exports/{exportId}", "GET").Produces<ApiResponse<PricingExportStatus>>();
        group.MapGet("/{exportId:guid}/artifact", async (Guid exportId, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetPricingExportArtifact(exportId), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/pricing/exports/{exportId}/artifact", "GET").Produces<ApiResponse<PricingExportArtifact>>();
        group.MapPost("/{exportId:guid}/cancel", async (Guid exportId, PricingExportVersion request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new CancelPricingExport(exportId, request.ExpectedVersion), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/pricing/exports/{exportId}/cancel", "POST").Produces<ApiResponse<PricingExportStatus>>()
            .WithMetadata(new OperationDescription("pricing.export.cancel", "取消本人未发布的导出",
                new OperationSubjectRoute("PricingExport", "exportId", OperationSubjectIdKind.Uuid)));
        group.MapPost("/{exportId:guid}/retry", async (Guid exportId, PricingExportVersion request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new RetryPricingExport(exportId, request.ExpectedVersion), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/pricing/exports/{exportId}/retry", "POST").Produces<ApiResponse<PricingExportStatus>>(202)
            .WithMetadata(new OperationDescription("pricing.export.retry", "恢复本人原导出委托",
                new OperationSubjectRoute("PricingExport", "exportId", OperationSubjectIdKind.Uuid)));
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message, statusCode: error.Code switch
    {
        "pricing.export.not_found" => StatusCodes.Status404NotFound,
        "pricing.export.unauthenticated" => StatusCodes.Status401Unauthorized,
        "pricing.export.request_conflict" or "pricing.export.cancel_conflict" or "pricing.export.retry_conflict" => StatusCodes.Status409Conflict,
        "pricing.export.unavailable" or "pricing.export.files_unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PricingExportVersion([property: JsonRequired] long ExpectedVersion);

// 依赖解析即校验证书与 HTTPS 配置；不把一次远程探测当成持续可用性证明。
internal sealed class PricingExportStartup(IExportFiles files) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(files); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
