using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>声明统一错误结构；允许的 HTTP 状态仍由各模块决定。</summary>
public static class ApiResponseMetadata
{
    /// <summary>为端点或分组声明错误响应，不改变运行时状态映射。</summary>
    /// <typeparam name="TBuilder">端点约定构建器类型。</typeparam>
    /// <param name="builder">端点或分组。</param>
    /// <param name="statusCodes">此边界可能返回的错误状态。</param>
    /// <returns>同一个构建器。</returns>
    public static TBuilder ProducesApiErrors<TBuilder>(this TBuilder builder, params int[] statusCodes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(statusCodes);
        foreach (var status in statusCodes)
        {
            builder.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblemDetails), ["application/problem+json"]));
        }
        return builder;
    }
}
