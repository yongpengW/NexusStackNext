using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NexusStackNext.Aspire.ServiceDefaults;

/// <summary>
/// 每个宿主共有的一层：**OpenTelemetry 追踪、健康检查、HTTP 韧性、服务发现**。
///
/// <para><b>它刻意不依赖 Aspire。</b>ADR-0005 的后果之一写着"不把 Aspire 的注入方式写进业务代码"——
/// 而这一层如果以 <c>AddServiceDefaults()</c>（Aspire 的那个扩展名）出现，
/// 就等于把"这个服务跑在 Aspire 里"变成了它的前提。</para>
///
/// <para>所以这里的每一个开关都由**配置**决定：有 OTLP 端点就导出，没有就只是不导出。
/// 服务发现同理——<c>ServiceDiscovery</c> 在标准 DNS 下也能工作，不要求 AppHost 在场。</para>
///
/// <para><b>可验证的推论</b>：把整个 <c>aspire/</c> 目录删掉，两个宿主仍然构建、仍然运行。
/// 那条推论在票据 14 里是**实测**过的。</para>
/// </summary>
public static class ServiceDefaults
{
    /// <summary>OTLP 端点所在的配置键（与 OpenTelemetry 的环境变量名一致）。</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>服务名所在的配置键。</summary>
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";

    /// <summary>接上这一层。</summary>
    /// <param name="builder">宿主构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IHostApplicationBuilder AddNexusStackServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var serviceName = builder.Configuration[ServiceNameKey]
            ?? builder.Environment.ApplicationName;

        var otlpEndpoint = builder.Configuration[OtlpEndpointKey];

        var telemetry = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName));

        telemetry
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                // **没有端点就不导出，而不是崩。**
                // "配了才生效"与"没配就起不来"是两件事——参照仓库的网关死在后者。
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(OtlpEndpoint(otlpEndpoint)));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(OtlpEndpoint(otlpEndpoint)));
                }
            });

        // 健康检查：**这一层不注册具体的检查项**，各宿主自己加（它们才知道自己依赖什么）。
        builder.Services.AddHealthChecks();

        // HTTP 韧性：出站调用的重试、超时、熔断。
        // `AddStandardResilienceHandler` 是官方那套默认值——够用，而且比手写一组"看起来合理"的数字更可能真的合理。
        builder.Services
            .AddHttpClient(string.Empty)
            .AddStandardResilienceHandler();

        // 服务发现：本地是一串地址，生产是 DNS。它在两种形态下都不需要 Aspire 在场。
        builder.Services.AddServiceDiscovery();

        return builder;
    }

    /// <summary>
    /// 把端点规整成 OTLP 的完整路径。
    ///
    /// <para>Seq 的 OTLP 接收端在 <c>/ingest/otlp</c>，而协议路径是 <c>/v1/traces</c> 与
    /// <c>/v1/metrics</c>——<b>两个信号各有一条路径</b>。OpenTelemetry 的 <c>Endpoint</c>
    /// 属性在"一个端点两种信号"时会把信号名拼在末尾，而那个拼接规则正是这里要保证的：
    /// 给一个不含信号后缀的基地址，让它自己拼。</para>
    /// </summary>
    /// <param name="configured">配置里给的端点。</param>
    /// <returns>规整后的端点。</returns>
    private static string OtlpEndpoint(string configured)
    {
        var trimmed = configured.TrimEnd('/');

        // 已经带了信号路径就直接用——那是调用方明确指定了，不该被改写。
        if (trimmed.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/";
    }
}
