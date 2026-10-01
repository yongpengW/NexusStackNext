using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;

namespace NexusStackNext.Scheduling.Endpoints;

/// <summary>
/// Scheduling 模块：本上下文对宿主暴露的全部内容——DI 注册、后台服务与 HTTP 端点。
///
/// <para>它是几个平台能力里唯一带真实后台服务的：一个只声明"何时触发"的上下文
/// 如果连触发都不做，就只是目录结构。</para>
///
/// <para>模块边界见 <c>NexusStackNext.Auditing.Endpoints.AuditingModule</c> 的说明（ADR-0013）。</para>
/// </summary>
public static class SchedulingModule
{
    /// <summary>注册本模块需要的服务，包含它自己的后台服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSchedulingInMemoryStorage();

        // 真实的后台服务，不是空壳。
        services.AddHostedService<SchedulingWorker>();

        return services;
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // 信息面**显式声明公开**（与边缘的 `scheduling-info` 一致）：公开必须写下来，
        // 而不是靠"忘了标注"这种默认放行。
        endpoints.MapGet("/api/scheduling", (ApiResponses responses, IClock clock) => responses.Ok(new
        {
            context = "scheduling",
            responsibility = "什么任务该在什么时候跑",
            tickIntervalSeconds = SchedulingWorker.TickInterval.TotalSeconds,
            at = clock.UtcNow,
        })).AllowAnonymous();

        // **管理面在进程内也要求认证**（与边缘的 `scheduling-management` 一致）。
        // 此前这四个端点在进程内没有任何授权判定——"边缘是唯一入口"是编排的事实，不是代码的事实。
        var tasks = endpoints.MapGroup("/api/scheduling/tasks").RequireAuthorization().ProducesApiErrors(400, 401, 403, 500);

        // 按稳定 ID 分页列出任务。**这是"调度器真的在跑"的可观察证据**：
        // 任务被执行过一次之后，lastRunAt 会被写上、nextRunAt 会向前推进。
        tasks.MapGet("/", async (ApiResponses responses, TaskRegistry registry, [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (!paging.IsValid)
            {
                return Failure(new Error(ApiPageRequest.InvalidErrorCode, ApiPageRequest.InvalidErrorMessage));
            }
            var all = await registry.ListAsync(cancellationToken);

            return responses.Page(all.OrderBy(static task => task.Id.Value)
                .Skip((int)Math.Min(paging.Offset, all.Count)).Take(paging.Limit)
                .Select(static task => new TaskItem(task.Id.Value, task.Code.Value, task.Interval.TotalSeconds,
                    task.IsEnabled, task.LastRunAt, task.NextRunAt)).ToArray(), all.Count, paging);
        }).Produces<ApiPage<TaskItem>>();

        // 定义一个任务。首次执行时刻不传就以"现在"起算——
        // 于是定义完，下一个调度节拍就会触发它。
        tasks.MapPost("/", async (ApiResponses responses,
            DefineTaskRequest request,
            TaskRegistry registry,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var code = TaskCode.Create(request.Code);
            if (code.IsFailure)
            {
                return Failure(code.Error);
            }

            if (request.IntervalSeconds <= 0)
            {
                return Failure(new Error("scheduling.interval.invalid", "执行间隔必须为正。"));
            }

            var defined = await registry.DefineAsync(
                code.Value,
                TimeSpan.FromSeconds(request.IntervalSeconds),
                request.FirstRunInSeconds is { } delay ? clock.UtcNow + TimeSpan.FromSeconds(delay) : null,
                cancellationToken);

            return defined.IsFailure
                ? Failure(defined.Error)
                : responses.Created($"/api/scheduling/tasks/{defined.Value.Id.Value}", new TaskCreatedResponse(defined.Value.Id.Value, defined.Value.Code.Value));
        }).ProducesApiErrors(415).Produces<ApiResponse<TaskCreatedResponse>>(201);

        tasks.MapPost("/{id:long}/pause", async (
            long id,
            TaskRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var paused = await registry.PauseAsync(new ScheduledTaskId(id), cancellationToken);
            return paused.IsFailure ? Failure(paused.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404);

        tasks.MapPost("/{id:long}/resume", async (
            long id,
            TaskRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var resumed = await registry.ResumeAsync(new ScheduledTaskId(id), cancellationToken);
            return resumed.IsFailure ? Failure(resumed.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404);

        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para>与 Files、Platform 的**故意不同**：这里 <c>scheduling.task.not_found</c> 是 404。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code == "scheduling.task.not_found"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>定义一个计划任务。</summary>
/// <param name="Code">任务编码。</param>
/// <param name="IntervalSeconds">执行间隔（秒）。</param>
/// <param name="FirstRunInSeconds">首次执行距现在多少秒；不传则以"现在"起算。</param>
internal sealed record DefineTaskRequest(string Code, double IntervalSeconds, double? FirstRunInSeconds);

internal sealed record TaskCreatedResponse(long TaskId, string Code);
internal sealed record TaskItem(long TaskId, string Code, double IntervalSeconds, bool IsEnabled, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);
