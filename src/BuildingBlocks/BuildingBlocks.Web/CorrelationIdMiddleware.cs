using Microsoft.AspNetCore.Builder;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>关联 ID 的头名与生成规则。</summary>
public static class CorrelationId
{
    /// <summary>随请求转发、随响应返回的头名。</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>读取或生成关联 ID。</summary>
    /// <param name="incoming">请求里带来的值。</param>
    /// <returns>可直接使用的关联 ID。</returns>
    public static string Resolve(string? incoming) =>
        incoming is { Length: > 0 and <= 64 }
        && incoming.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? incoming : Guid.NewGuid().ToString("N");
}

/// <summary>关联 ID 中间件。</summary>
public static class CorrelationIdMiddleware
{
    /// <summary>
    /// 为每个请求准备一个有界关联 ID：仅沿用安全字符组成的值，其余重新生成，
    /// 写回请求头（于是 YARP 会把它转发给后端），并在响应头里返回。
    /// <para>
    /// 网关与来源宿主使用同一规则；关联值仅用于调查，不能证明身份或作为幂等键。
    /// </para>
    /// </summary>
    /// <param name="app">应用管线。</param>
    /// <returns>同一个管线，便于链式调用。</returns>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var correlationId = CorrelationId.Resolve(context.Request.Headers[CorrelationId.HeaderName]);
            context.Request.Headers[CorrelationId.HeaderName] = correlationId;

            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationId.HeaderName] = correlationId;
                return Task.CompletedTask;
            });

            await next().ConfigureAwait(false);
        });
    }
}
