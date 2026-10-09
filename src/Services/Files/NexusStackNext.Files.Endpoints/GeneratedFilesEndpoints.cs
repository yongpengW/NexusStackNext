using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;

namespace NexusStackNext.Files.Endpoints;

/// <summary>版本化的生产者协议；只接受宿主直接验证的 TLS 客户端。</summary>
public static class GeneratedFilesEndpoints
{
    /// <summary>映射不属于公共 API 前缀的生产者入口。</summary>
    /// <param name="endpoints">路由构建器。</param>
    /// <returns>原构建器。</returns>
    public static IEndpointRouteBuilder MapGeneratedFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.MapGroup("/internal/files/v1/uploads").RequireAuthorization(FilesProducerAccess.Scheme).ExcludeFromDescription();
        routes.MapPost("/{uploadId:guid}", async (Guid uploadId, GeneratedFileDescriptionV1 request,
            HttpContext context, GeneratedFileService files, ApiResponses responses, CancellationToken token) =>
        {
            var result = await files.RegisterAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, uploadId, request, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : FilesModule.Failure(result.Error);
        }).WithMetadata(new OperationDescription("files.candidate.register", "登记私有成果候选"));
        routes.MapPut("/{uploadId:guid}/content", async (Guid uploadId, HttpContext context,
            GeneratedFileService files, GeneratedFileOptions options, ApiResponses responses, CancellationToken token) =>
        {
            if (context.Request.ContentLength > options.MaxBytes) { return FilesModule.Failure(new Error("files.too_large", "内容超过成果上限。")); }
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) { bodyLimit.MaxRequestBodySize = options.MaxBytes; }
            var result = await files.SealAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, uploadId, context.Request.Body, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Created($"/internal/files/v1/uploads/{uploadId}", result.Value) : FilesModule.Failure(result.Error);
        }).WithMetadata(new OperationDescription("files.candidate.seal", "封存完整私有成果"));
        routes.MapGet("/{uploadId:guid}", async (Guid uploadId, HttpContext context,
            GeneratedFileService files, ApiResponses responses, CancellationToken token) =>
        {
            var receipt = await files.ReadAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, uploadId, token).ConfigureAwait(false);
            return receipt is null ? FilesModule.Failure(new Error("files.not_found", "候选不存在。")) : (IResult)responses.Ok(receipt);
        });
        routes.MapPost("/{uploadId:guid}/publish", async (Guid uploadId, GeneratedFilePublicationV1 request, HttpContext context,
            GeneratedFileService files, ApiResponses responses, CancellationToken token) =>
        {
            var receipt = await files.PublishAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, uploadId, request.PublicationId, token).ConfigureAwait(false);
            return receipt.IsSuccess ? (IResult)responses.Ok(receipt.Value) : FilesModule.Failure(receipt.Error);
        }).WithMetadata(new OperationDescription("files.candidate.publish", "发布私有成果"));
        routes.MapGet("/{uploadId:guid}/availability", async (Guid uploadId, HttpContext context,
            GeneratedFileService files, ApiResponses responses, CancellationToken token) =>
        {
            var available = await files.AvailabilityAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, uploadId, token).ConfigureAwait(false);
            return available is null ? FilesModule.Failure(new Error("files.not_found", "候选不存在。")) : (IResult)responses.Ok(available);
        });
        endpoints.MapGet("/internal/files/v1/publications/{publicationId:guid}", async (Guid publicationId, HttpContext context,
            GeneratedFileService files, ApiResponses responses, CancellationToken token) =>
        {
            var receipt = await files.ReadPublicationAsync(context.User.FindFirstValue(FilesProducerAccess.ProducerClaim)!, publicationId, token).ConfigureAwait(false);
            return receipt is null ? FilesModule.Failure(new Error("files.not_found", "发布裁决不存在。")) : (IResult)responses.Ok(receipt);
        }).RequireAuthorization(FilesProducerAccess.Scheme).ExcludeFromDescription();
        return endpoints;
    }

}
