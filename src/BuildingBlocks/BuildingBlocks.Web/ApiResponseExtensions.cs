using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>由每个宿主显式注册 HTTP 响应契约。</summary>
public static class ApiResponseExtensions
{
    /// <summary>注册结果适配器及其请求、时间依赖。</summary>
    /// <param name="services">宿主服务。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddApiResponseContract(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new HttpInt64Converter()));
        services.ConfigureAll<OpenApiOptions>(options =>
        {
            options.AddSchemaTransformer<HttpInt64OpenApi>();
            options.AddDocumentTransformer<HttpInt64OpenApi>();
        });
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ApiResponses>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProblemDetailsWriter, ApiProblemDetailsWriter>());
        services.AddProblemDetails();
        services.AddExceptionHandler(options => options.StatusCodeSelector = static error =>
            error is CommittedFactCapacityBusyException ? StatusCodes.Status503ServiceUnavailable
            : error is BadHttpRequestException badRequest ? badRequest.StatusCode : StatusCodes.Status500InternalServerError);
        return services;
    }

    /// <summary>返回与正文对应的追踪标识，不缓冲或改写响应流。</summary>
    /// <param name="app">宿主请求管线。</param>
    /// <returns>同一个管线。</returns>
    public static IApplicationBuilder UseApiResponseContract(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePages();
        return app.Use(async (context, next) =>
        {
            var traceId = ApiResponses.TraceId(context);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-TraceId"] = traceId;
                return Task.CompletedTask;
            });
            await next().ConfigureAwait(false);
        });
    }
}
