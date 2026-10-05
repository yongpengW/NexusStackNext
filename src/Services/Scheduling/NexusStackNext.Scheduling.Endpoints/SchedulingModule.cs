using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;
using NexusStackNext.Scheduling.Infrastructure;
using NexusStackNext.Scheduling.Infrastructure.Persistence;

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
    /// <param name="configuration">存储配置。</param>
    /// <param name="environment">运行环境。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var capacityRead = configuration.GetSection("Scheduling:AuditDelivery:CapacityRead").Get<CommittedFactCapacityReadOptions>() ?? new();
        capacityRead.Validate();
        services.AddSingleton<IScheduleCalendar, CronScheduleCalendar>();
        services.AddHostedService<SchedulingCalendarRuntimeCheck>();
        services.AddHealthChecks().AddCheck<SchedulingCalendarRuntimeCheck>("scheduling-calendar");
        var provider = configuration["Scheduling:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) { provider = "Postgres"; }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Scheduling:Storage:Provider=Memory 仅允许 Development / Testing 环境。");
            }
            services.AddSchedulingInMemoryStorage(configuration.GetSection("Scheduling:AuditDelivery:MemoryCapacity").Get<MemoryCommittedFactCapacityOptions>(),
                configuration.GetSection("Scheduling:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>(),
                configuration.GetSection("Scheduling:AuditDelivery:MemoryPolicyControl").Get<MemoryFactCapacityPolicyControlOptions>());
            services.AddSchedulingMemoryFactCleanup(configuration.GetSection("Scheduling:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("Scheduling");
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("必须配置 ConnectionStrings:Scheduling；开发测试可显式选择 Scheduling:Storage:Provider=Memory。");
            }
            var capacityWrite = configuration.GetSection("Scheduling:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>() ?? new();
            services.AddSchedulingPostgresStorage(capacityWrite.ConfigureConnection(connection));
            services.AddKeyedScoped<PostgresFactCapacityPolicyStore>("scheduling", (provider, _) => new(
                capacityWrite.ConfigureConnection(connection), provider.GetRequiredService<IIntegrationEventSerializer>(), capacityRead.Timeout,
                new("scheduling", SchedulingFactCapacityPolicyChangedV1.From)));
            services.AddKeyedScoped<ICommittedFactCapacityPolicyStore>("scheduling", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("scheduling"));
            services.AddKeyedScoped<ICommittedFactCapacityPolicyCleanup>("scheduling", (provider, _) => provider.GetRequiredKeyedService<PostgresFactCapacityPolicyStore>("scheduling"));
            services.AddCommittedFactCapacityReader<SchedulingDbContext>("scheduling", capacityRead);
            services.AddCommittedFactCleanup<SchedulingDbContext>("scheduling", PlanCommittedV1.Name,
                configuration.GetSection("Scheduling:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        }
        else { throw new InvalidOperationException("Scheduling:Storage:Provider 仅支持 Postgres / Memory。"); }

        // 真实的后台服务，不是空壳。
        if (configuration.GetValue("Scheduling:Worker:Enabled", true)) { services.AddHostedService<SchedulingWorker>(); }

        return services.AddCommittedFactPolicyMaintenance("scheduling", configuration.GetSection("Scheduling:AuditDelivery:PolicyMaintenance")
            .Get<FactCapacityPolicyMaintenanceOptions>());
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

        // 计划管理是后台执行委托：认证之外还须检查操作权限和当前会话。
        var tasks = endpoints.MapGroup("/api/scheduling/tasks").RequireAuthorization().ProducesApiErrors(400, 401, 403, 409, 500, 503);
        endpoints.MapGet("/api/scheduling/audit-capacity", async ([FromKeyedServices("scheduling")] ICommittedFactCapacityPolicyStore policies,
            ApiResponses responses, CancellationToken token) =>
        {
            var result = await policies.ReadPolicyAsync(token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthorization().AddEndpointFilter<NexusStackAuthorizationFilter>()
            .RequirePermission("/api/scheduling/audit-capacity", "GET")
            .Produces<ApiResponse<FactCapacityPolicySnapshot>>().ProducesApiErrors(401, 403, 503);
        endpoints.MapPut("/api/scheduling/audit-capacity", async (FactCapacityPolicyRequest request,
            ICurrentUser user, IClock clock, IExecutionContext execution, ApiResponses responses, CancellationToken token,
            [FromKeyedServices("scheduling")] ICommittedFactCapacityPolicyStore policies) =>
        {
            var result = await policies.AdjustAsync(request, user.UserId ?? string.Empty, clock.UtcNow,
                execution.Capture(), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthorization().AddEndpointFilter<NexusStackAuthorizationFilter>()
            .RequirePermission("/api/scheduling/audit-capacity", "PUT")
            .Produces<ApiResponse<FactCapacityPolicyReceipt>>().ProducesApiErrors(400, 401, 403, 409, 415, 503)
            .WithMetadata(new OperationDescription("scheduling.fact-capacity-policy.adjust", "调整所属事实容量策略"));
        tasks.AddEndpointFilter<NexusStackAuthorizationFilter>();

        tasks.MapPost("/preview", (PreviewScheduleRequest request, IScheduleCalendar calendar, ApiResponses responses) =>
        {
            if (request.Rule is null) { return Failure(new Error("scheduling.rule.invalid", "必须指定计划规则。")); }
            if (!request.TryGetAfter(out var after)) { return Failure(new Error("scheduling.preview.invalid", "必须指定包含 Z 或显式偏移的 ISO 日期时间起点。")); }
            var preview = calendar.Preview(request.Rule, after, request.Count);
            return preview.IsSuccess ? (IResult)responses.Ok(preview.Value) : Failure(preview.Error);
        }).Produces<ApiResponse<SchedulePreview>>().ProducesApiErrors(415)
            .RequirePermission("/api/scheduling/tasks/preview", "POST");

        var occurrences = endpoints.MapGroup("/api/scheduling/occurrences").RequireAuthorization().ProducesApiErrors(400, 401, 403, 409, 500, 503);
        occurrences.AddEndpointFilter<NexusStackAuthorizationFilter>();
        occurrences.MapPost("/{id:guid}/retry", async (Guid id, RetryOccurrenceRequest request, IScheduledTaskStore store,
            ApiResponses responses, CancellationToken cancellationToken) =>
        {
            var result = await store.RetryOccurrenceAsync(id, request.ExpectedDeadLetteredAt, cancellationToken);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).Produces<ApiResponse<ScheduleOccurrenceDelivery>>(202).ProducesApiErrors(415)
            .RequirePermission("/api/scheduling/occurrences/{id}/retry", "POST");

        tasks.MapGet("/{id:long}/occurrences", async (long id, IScheduledTaskStore store, ApiResponses responses,
            [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (!paging.IsValid || paging.Page > TaskRegistry.MaximumPage || paging.Limit > 100) { return Failure(new Error(ApiPageRequest.InvalidErrorCode, "page 必须在 1 到 1000，触发历史 limit 必须在 1 到 100 之间。")); }
            var page = await store.ReadOccurrencesAsync(id, paging.Offset, paging.Limit, cancellationToken);
            return responses.Page(page.Items, page.Total, paging);
        }).Produces<ApiPage<ScheduleOccurrenceDelivery>>().RequirePermission("/api/scheduling/tasks/{id}/occurrences", "GET");

        tasks.MapGet("/{id:long}/decisions", async (long id, IScheduledTaskStore store, ApiResponses responses,
            [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (!paging.IsValid || paging.Page > TaskRegistry.MaximumPage || paging.Limit > 100) { return Failure(new Error(ApiPageRequest.InvalidErrorCode, "page 必须在 1 到 1000，调度决定 limit 必须在 1 到 100 之间。")); }
            var page = await store.ReadDecisionsAsync(id, paging.Offset, paging.Limit, cancellationToken);
            return responses.Page(page.Items, page.Total, paging);
        }).Produces<ApiPage<ScheduleDecision>>().RequirePermission("/api/scheduling/tasks/{id}/decisions", "GET");

        // 按稳定 ID 分页列出任务。**这是"调度器真的在跑"的可观察证据**：
        // 任务被执行过一次之后，lastRunAt 会被写上、nextRunAt 会向前推进。
        tasks.MapGet("/", async (ApiResponses responses, IScheduledTaskStore store, [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (!paging.IsValid || paging.Page > TaskRegistry.MaximumPage)
            {
                return Failure(new Error(ApiPageRequest.InvalidErrorCode, "page 必须在 1 到 1000，limit 必须在 1 到 200 之间。"));
            }
            var page = await store.ReadPageAsync(paging.Page, paging.Limit, cancellationToken);

            return responses.Page(page.Items
                .Select(static task => new TaskItem(task.Id.Value, task.Code.Value, task.Interval?.TotalSeconds,
                    task.IsEnabled, task.LastRunAt, task.NextRunAt, task.Target.Kind, task.Target.SubjectId,
                    task.DelegatedBy, task.Version, task.Rule, task.ScheduleRevision, task.RetryAt,
                    task.LastSchedulingErrorCode, task.SchedulingFailureCount, EntityAuditMetadata.From(task))).ToArray(), page.Total, paging);
        }).Produces<ApiPage<TaskItem>>().RequirePermission("/api/scheduling/tasks", "GET");

        // 固定间隔未指定首次延迟时立即到期；日历计划使用明确规则的下一发生。
        tasks.MapPost("/", async (ApiResponses responses,
            DefineTaskRequest request,
            TaskRegistry registry,
            IClock clock,
            ICurrentUser currentUser,
            CancellationToken cancellationToken) =>
        {
            var code = TaskCode.Create(request.Code);
            if (code.IsFailure)
            {
                return Failure(code.Error);
            }

            var target = ScheduleTarget.Create(request.TargetKind, request.TargetId);
            if (target.IsFailure) { return Failure(target.Error); }
            if (request.Rule is not null)
            {
                if (request.IntervalSeconds is not null || request.FirstRunInSeconds is not null)
                {
                    return Failure(new Error("scheduling.rule.invalid", "rule 不能与旧固定间隔或首次延迟字段混用。"));
                }
                var rulePlan = await registry.DefineAsync(code.Value, request.Rule, target.Value,
                    currentUser.UserId ?? string.Empty, cancellationToken);
                return rulePlan.IsFailure ? Failure(rulePlan.Error)
                    : responses.Created($"/api/scheduling/tasks/{rulePlan.Value.Id.Value}", new TaskCreatedResponse(rulePlan.Value.Id.Value, rulePlan.Value.Code.Value));
            }
            if (request.IntervalSeconds is not { } intervalSeconds || !double.IsFinite(intervalSeconds) || intervalSeconds < ScheduledTask.MinimumInterval.TotalSeconds
                || intervalSeconds > ScheduledTask.MaximumInterval.TotalSeconds)
            {
                return Failure(new Error("scheduling.interval.invalid", "执行间隔必须在 1 秒到 366 天之间。"));
            }
            if (request.FirstRunInSeconds is { } firstRun && (!double.IsFinite(firstRun) || firstRun < 0
                || firstRun > ScheduledTask.MaximumInterval.TotalSeconds))
            {
                return Failure(new Error("scheduling.first_run.invalid", "首次延迟必须在 0 秒到 366 天之间。"));
            }

            var defined = await registry.DefineAsync(
                code.Value,
                ScheduleRule.NormalizeInterval(intervalSeconds),
                target.Value, currentUser.UserId ?? string.Empty,
                request.FirstRunInSeconds is { } delay ? clock.UtcNow + TimeSpan.FromSeconds(delay) : null,
                cancellationToken);

            return defined.IsFailure
                ? Failure(defined.Error)
                : responses.Created($"/api/scheduling/tasks/{defined.Value.Id.Value}", new TaskCreatedResponse(defined.Value.Id.Value, defined.Value.Code.Value));
        }).ProducesApiErrors(415, 503).Produces<ApiResponse<TaskCreatedResponse>>(201)
            .RequirePermission("/api/scheduling/tasks", "POST");

        tasks.MapPut("/{id:long}/rule", async (long id, UpdateScheduleRuleRequest request, TaskRegistry registry, CancellationToken cancellationToken) =>
        {
            if (request.Rule is null) { return Failure(ScheduleRule.Invalid); }
            var result = await registry.UpdateRuleAsync(new ScheduledTaskId(id), request.ExpectedVersion, request.Rule, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
        }).Produces(204).ProducesApiErrors(404, 409, 415, 503).RequirePermission("/api/scheduling/tasks/{id}/rule", "PUT");

        tasks.MapPost("/{id:long}/pause", async (
            long id,
            ChangeEnabledRequest request,
            TaskRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var paused = await registry.PauseAsync(new ScheduledTaskId(id), request.ExpectedVersion, cancellationToken);
            return paused.IsFailure ? Failure(paused.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404, 409, 415, 503).RequirePermission("/api/scheduling/tasks/{id}/pause", "POST");

        tasks.MapPost("/{id:long}/resume", async (
            long id,
            ChangeEnabledRequest request,
            TaskRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var resumed = await registry.ResumeAsync(new ScheduledTaskId(id), request.ExpectedVersion, cancellationToken);
            return resumed.IsFailure ? Failure(resumed.Error) : Results.NoContent();
        }).Produces(204).ProducesApiErrors(404, 409, 415, 503).RequirePermission("/api/scheduling/tasks/{id}/resume", "POST");

        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para>与 Files、Platform 的**故意不同**：这里 <c>scheduling.task.not_found</c> 是 404。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code switch
        {
            "scheduling.task.not_found" => StatusCodes.Status404NotFound,
            "scheduling.audit_policy.control_exhausted" or "scheduling.audit_capacity_exhausted" or "audit_capacity.unavailable" or "audit_capacity.busy" => StatusCodes.Status503ServiceUnavailable,
            "scheduling.audit_policy.conflict" or "scheduling.version_conflict" or "scheduling.task_code.taken" or "scheduling.delivery_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        },
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>定义一个计划任务。</summary>
/// <param name="Code">任务编码。</param>
/// <param name="IntervalSeconds">执行间隔（秒）。</param>
/// <param name="FirstRunInSeconds">首次执行距现在多少秒；不传则以"现在"起算。</param>
/// <param name="TargetKind">受支持的目标操作。</param>
/// <param name="TargetId">目标对象标识。</param>
/// <param name="Rule">显式计划规则；与旧间隔和首次延迟字段互斥。</param>
internal sealed record DefineTaskRequest(string Code, double? IntervalSeconds, double? FirstRunInSeconds, string? TargetKind, Guid TargetId, ScheduleRuleInput? Rule);

internal sealed record TaskCreatedResponse(long TaskId, string Code);
internal sealed record PreviewScheduleRequest(ScheduleRuleInput? Rule,
    [property: System.ComponentModel.Description("ISO 日期时间，必须包含 Z 或显式偏移；预览从此时刻之后开始。")]
    string? After, int Count = 1)
{
    public bool TryGetAfter(out DateTimeOffset instant)
    {
        instant = default;
        return After is { Length: >= 20 } value
            && (value.EndsWith('Z') || value.Length >= 25 && value[^6] is '+' or '-' && value[^3] == ':')
            && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out instant);
    }
}
internal sealed record ChangeEnabledRequest(long ExpectedVersion);
internal sealed record UpdateScheduleRuleRequest(long ExpectedVersion, ScheduleRuleInput? Rule);
internal sealed record RetryOccurrenceRequest(DateTimeOffset ExpectedDeadLetteredAt);
internal sealed record TaskItem(long TaskId, string Code, double? IntervalSeconds, bool IsEnabled, DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt, string TargetKind, Guid TargetId, string CreatedBy, long Version, ScheduleRule Rule, long ScheduleRevision,
    DateTimeOffset? RetryAt, string? LastSchedulingErrorCode, int SchedulingFailureCount, EntityAuditMetadata? Audit);
