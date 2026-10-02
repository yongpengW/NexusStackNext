using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.Auditing.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Web;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>来源宿主显式接入操作采集；业务模块不依赖该实现。</summary>
public static class OperationJournalModule
{
    /// <summary>配置独立 journal；业务事务回滚不撤销操作观察。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">来源配置。</param>
    /// <param name="environment">运行环境。</param>
    /// <param name="source">代码指定的来源，不从 HTTP 读取。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddOperationJournalModule(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, string source)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 64 || source.Any(char.IsControl)) { throw new ArgumentException("操作来源无效。", nameof(source)); }
        var timeout = configuration.GetValue("OperationJournal:WriteTimeout", TimeSpan.FromSeconds(2));
        if (timeout < TimeSpan.FromMilliseconds(50) || timeout > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("OperationJournal:WriteTimeout 必须在 50 毫秒到 5 秒之间。");
        }
        services.AddSingleton(new OperationCaptureOptions(source, timeout));
        services.AddTransient<OperationLoggingMiddleware>();
        var provider = configuration["OperationJournal:Storage:Provider"] ?? "Postgres";
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("OperationJournal:Storage:Provider=Memory 仅允许开发测试使用。");
            }
            return services.AddOperationJournalMemoryStorage();
        }
        if (!string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OperationJournal:Storage:Provider 仅支持 Postgres / Memory。");
        }
        var connection = configuration.GetConnectionString("OperationJournal");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("必须配置 ConnectionStrings:OperationJournal；开发测试可显式选择 OperationJournal:Storage:Provider=Memory。");
        }
        return services.AddOperationJournalPostgresStorage(connection);
    }

    /// <summary>在路由后、异常处理和认证授权前捕获，观察完整的 HTTP 处理结果。</summary>
    /// <param name="app">宿主管线。</param>
    /// <returns>宿主管线。</returns>
    public static IApplicationBuilder UseOperationJournal(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<OperationLoggingMiddleware>();
    }

    /// <summary>独立 CLI 入口；不启动 Web、broker 或业务模块。</summary>
    /// <returns>进程退出码。</returns>
    public static async Task<int> MigrateOperationJournalAsync()
    {
        try
        {
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__OperationJournal");
            if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await OperationJournalDatabase.MigrateAsync(connection, timeout.Token).ConfigureAwait(false);
            Console.WriteLine("OperationJournal migrations applied.");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("OperationJournal migration failed; check ConnectionStrings__OperationJournal, database availability and migration privileges.");
            return 1;
        }
    }
}

internal sealed record OperationCaptureOptions(string Source, TimeSpan WriteTimeout);

internal sealed partial class OperationLoggingMiddleware(IOperationJournal journal, OperationJournalStatus status,
    OperationCaptureOptions options, IClock clock, ILogger<OperationLoggingMiddleware> logger) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.GetEndpoint()?.Metadata.GetMetadata<OperationLogSuppression>() is not null
            || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/gateway/openapi", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var operationId = Guid.NewGuid();
        var started = new OperationObservedV1
        {
            EventId = Guid.NewGuid(),
            OperationId = operationId,
            Source = options.Source,
            Kind = "http",
            Phase = "started",
            OccurredAt = clock.UtcNow,
            TraceId = Activity.Current?.TraceId.ToString() ?? operationId.ToString("N"),
            HttpMethod = ClassifyMethod(context.Request.Method),
            RouteTemplate = SafeRouteTemplate(context.GetEndpoint() as RouteEndpoint),
            Metadata = Describe(context),
        };
        var timer = Stopwatch.StartNew();
        await RecordAsync(started).ConfigureAwait(false);
        string? interrupted = null;
        try { await next(context).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            interrupted = "canceled";
            throw;
        }
        catch (Exception)
        {
            interrupted = "failed";
            throw;
        }
        finally
        {
            // 异常处理器会把客户端断开转换成 499 或已开始的响应；它可能不再向外抛异常。
            if (context.RequestAborted.IsCancellationRequested) { interrupted = "canceled"; }
            var httpStatus = interrupted is null || interrupted == "canceled" ? context.Response.StatusCode : (int?)null;
            var actor = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            // 身份无法满足安全元数据约束时省略；不把未经认证的请求字段当成替代身份。
            if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl)) { actor = null; }
            await RecordAsync(started with
            {
                EventId = Guid.NewGuid(),
                Phase = "finished",
                OccurredAt = clock.UtcNow,
                ActorId = actor,
                StatusCode = httpStatus,
                DurationMs = timer.ElapsedMilliseconds,
                Outcome = interrupted ?? httpStatus switch
                {
                    StatusCodes.Status202Accepted => "accepted",
                    >= 500 => "failed",
                    >= 400 => "rejected",
                    _ => "completed",
                },
            }).ConfigureAwait(false);
        }
    }

    private static OperationDetails Describe(HttpContext context)
    {
        var declaration = context.GetEndpoint()?.Metadata.GetMetadata<OperationDescription>();
        var subject = declaration?.Subject;
        string? subjectId = null;
        string? subjectKind = null;
        if (subject is not null && context.Request.RouteValues[subject.RouteParameter] is string { Length: <= 64 } raw)
        {
            if (subject.Kind == OperationSubjectIdKind.Uuid && Guid.TryParse(raw, out var id) && id != Guid.Empty)
            {
                subjectId = id.ToString("D");
                subjectKind = "guid";
            }
            else if (subject.Kind == OperationSubjectIdKind.Numeric
                && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                subjectId = number.ToString(CultureInfo.InvariantCulture);
                subjectKind = "int64";
            }
        }
        var span = Activity.Current?.SpanId.ToString();
        var parent = Activity.Current?.ParentSpanId.ToString();
        return new OperationDetails
        {
            Action = declaration?.Action ?? "http." + ClassifyMethod(context.Request.Method).ToLowerInvariant(),
            Description = declaration?.Description,
            ExecutionRole = declaration?.IsProxy == true ? "proxy" : "endpoint",
            SubjectType = subjectId is null ? null : subject!.Type,
            SubjectIdKind = subjectKind,
            SubjectId = subjectId,
            SpanId = span == "0000000000000000" ? null : span,
            ParentSpanId = parent == "0000000000000000" ? null : parent,
            CorrelationId = CorrelationId.Resolve(context.Request.Headers[CorrelationId.HeaderName]),
        };
    }

    private static string? SafeRouteTemplate(RouteEndpoint? endpoint)
    {
        var template = endpoint?.RoutePattern.RawText;
        return template is { Length: > 0 and <= 500 } && template.StartsWith('/') && !template.Any(char.IsControl)
            ? template : null;
    }

    private static string ClassifyMethod(string method) => method switch
    {
        "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "CONNECT" or "OPTIONS" or "TRACE" or "PATCH"
            or "PROPFIND" or "PROPPATCH" or "MKCOL" or "COPY" or "MOVE" or "LOCK" or "UNLOCK" or "M-SEARCH" => method,
        _ => "OTHER",
    };

    private async Task RecordAsync(OperationObservedV1 observation)
    {
        // RequestAborted 不取消日志落盘；每条写入自带独立且有界的超时。
        using var timeout = new CancellationTokenSource(options.WriteTimeout);
        try
        {
            if ((await journal.AppendAsync(observation, timeout.Token).ConfigureAwait(false)).IsSuccess) { return; }
        }
        catch (Exception) { /* 普通观察降级不能改变业务响应；原异常可能包含秘密，不输出。 */ }
        status.ReportFailure();
        try { LogCaptureFailed(observation.OperationId, observation.Phase); }
        catch (Exception) { /* 诊断提供器也可能故障；已登记的 health 失败计数仍然可见。 */ }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "操作观察未持久化：OperationId={OperationId}，Phase={Phase}；操作日志已降级。")]
    private partial void LogCaptureFailed(Guid operationId, string phase);
}
