using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using NexusStackNext.Auditing.Endpoints;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace NexusStackNext.Gateway;

/// <summary>处理代理故障证据和 YARP 2.3 路由超时，不从已发送状态码推断传输成功。</summary>
public static class ProxyFailureHandling
{
    /// <summary>在 UseRequestTimeouts 后注册；保留响应已开始后的故障，只对未开始的超时生成 504。</summary>
    /// <param name="app">宿主管线。</param>
    /// <returns>宿主管线。</returns>
    public static IApplicationBuilder UseProxyFailureHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is null)
            {
                await next().ConfigureAwait(false);
                return;
            }
            var lifetime = context.Features.Get<IHttpRequestLifetimeFeature>()!;
            var reset = context.Features.Get<IHttpResetFeature>();
            var termination = new ProxyTermination(lifetime);
            context.Features.Set<IHttpRequestLifetimeFeature>(termination);
            if (reset is not null) { context.Features.Set<IHttpResetFeature>(new ProxyReset(reset, termination)); }
            try
            {
                await next().ConfigureAwait(false);
                var timeout = context.Features.Get<IHttpRequestTimeoutFeature>();
                var error = context.Features.Get<IForwarderErrorFeature>()?.Error;
                var canceled = error is ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.ResponseBodyCanceled;
                var timedOut = timeout?.RequestTimeoutToken.IsCancellationRequested == true;
                if (error is ForwarderError.Request or ForwarderError.RequestCreation or ForwarderError.RequestTimedOut
                    or ForwarderError.RequestBodyDestination or ForwarderError.ResponseHeaders or ForwarderError.ResponseBodyDestination
                    or ForwarderError.NoAvailableDestinations
                    || canceled && (timedOut || termination.AbortedWhileConnected || !context.RequestAborted.IsCancellationRequested))
                {
                    context.MarkOperationFailed();
                }
                // YARP #2662：未开始的路由超时被转换成 400；交回框架，仍由它检查原始客户端 token。
                if (!context.Response.HasStarted && timedOut && canceled)
                {
                    throw new OperationCanceledException(timeout!.RequestTimeoutToken);
                }
            }
            finally
            {
                context.Features.Set(lifetime);
                if (reset is not null) { context.Features.Set(reset); }
            }
        });
    }

    // YARP 在传输失败后主动 Abort/Reset，也会取消 RequestAborted。
    // 在这个动作之前观察取消状态，才能区分内部 activity timeout 与客户端先行断开。
    private sealed class ProxyTermination(IHttpRequestLifetimeFeature inner) : IHttpRequestLifetimeFeature
    {
        public bool AbortedWhileConnected { get; private set; }
        public CancellationToken RequestAborted { get => inner.RequestAborted; set => inner.RequestAborted = value; }
        public void ObserveAbort() => AbortedWhileConnected |= !RequestAborted.IsCancellationRequested;
        public void Abort()
        {
            ObserveAbort();
            inner.Abort();
        }
    }

    private sealed class ProxyReset(IHttpResetFeature inner, ProxyTermination termination) : IHttpResetFeature
    {
        public void Reset(int errorCode)
        {
            termination.ObserveAbort();
            inner.Reset(errorCode);
        }
    }
}
