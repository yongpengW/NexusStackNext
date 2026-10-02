using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Forwarder;

namespace NexusStackNext.Gateway;

/// <summary>把 YARP 2.3 的路由超时取消交回 ASP.NET Core 超时处理中间件。</summary>
public static class ProxyTimeoutResponse
{
    /// <summary>在 UseRequestTimeouts 后注册；只处理已证实的超时且尚未开始的代理响应。</summary>
    /// <param name="app">宿主管线。</param>
    /// <returns>宿主管线。</returns>
    public static IApplicationBuilder UseProxyTimeoutResponse(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            await next().ConfigureAwait(false);
            var timeout = context.Features.Get<IHttpRequestTimeoutFeature>();
            var error = context.Features.Get<IForwarderErrorFeature>()?.Error;
            // YARP #2662：取消被转换成 400；框架只在收到取消异常时生成 504。
            // 外层超时处理器还会检查原始客户端 token，不把客户端断开当作超时。
            if (!context.Response.HasStarted && timeout?.RequestTimeoutToken.IsCancellationRequested == true
                && error is ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.ResponseBodyCanceled)
            {
                throw new OperationCanceledException(timeout.RequestTimeoutToken);
            }
        });
    }
}
