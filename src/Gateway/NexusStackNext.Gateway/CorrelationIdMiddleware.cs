namespace NexusStackNext.Gateway;

/// <summary>关联 ID 的头名与生成规则。</summary>
public static class CorrelationId
{
    /// <summary>随请求转发、随响应返回的头名。</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>读取或生成关联 ID。</summary>
    /// <param name="incoming">请求里带来的值。</param>
    /// <returns>可直接使用的关联 ID。</returns>
    public static string Resolve(string? incoming) =>
        string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("n") : incoming.Trim();
}

/// <summary>关联 ID 中间件。</summary>
public static class CorrelationIdMiddleware
{
    /// <summary>
    /// 为每个请求准备一个关联 ID：沿用调用方带来的，没有就生成一个，
    /// 写回请求头（于是 YARP 会把它转发给后端），并在响应头里返回。
    /// <para>
    /// 参照仓库完全没有关联 ID（review/03）：一次跨服务的调用链在日志里无法串起来。
    /// 这里它是网关的职责——边缘是唯一能看到完整请求的地方。
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
