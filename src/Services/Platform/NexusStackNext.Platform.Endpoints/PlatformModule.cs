using NexusStackNext.BuildingBlocks.Application.Auditing;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Contracts;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure;
using NexusStackNext.Platform.Infrastructure.Persistence;

namespace NexusStackNext.Platform.Endpoints;

/// <summary>
/// Platform 模块：本上下文对宿主暴露的全部内容——DI 注册与 HTTP 端点。
///
/// <para>Platform 是配置的**唯一真相**：其他上下文只按 key 读它，不 join 它的表。</para>
/// <para>模块边界见 <c>NexusStackNext.Auditing.Endpoints.AuditingModule</c> 的说明（ADR-0013）。</para>
/// </summary>
public static class PlatformModule
{
    /// <summary>注册本模块需要的服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">本模块的存储配置。</param>
    /// <param name="environment">运行环境；内存适配器只用于开发和测试。</param>
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var capacityRead = configuration.GetSection("Platform:AuditDelivery:CapacityRead").Get<CommittedFactCapacityReadOptions>() ?? new();
        capacityRead.Validate();
        var provider = configuration["Platform:Storage:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) { provider = "Postgres"; }
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Platform:Storage:Provider=Memory 仅允许 Development / Testing 环境。");
            }
            services.AddPlatformInMemoryStorage(configuration.GetSection("Platform:AuditDelivery:MemoryCapacity").Get<MemoryCommittedFactCapacityOptions>(),
                configuration.GetSection("Platform:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>());
            services.AddPlatformMemoryFactCleanup(configuration.GetSection("Platform:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        }
        else if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("必须配置 ConnectionStrings:Platform；开发测试可显式选择 Platform:Storage:Provider=Memory。");
            }
            var capacityWrite = configuration.GetSection("Platform:AuditDelivery:CapacityWrite").Get<CommittedFactCapacityWriteOptions>() ?? new();
            services.AddPlatformPostgresStorage(capacityWrite.ConfigureConnection(connection));
            services.AddCommittedFactCapacityReader<PlatformDbContext>("platform", capacityRead);
            services.AddCommittedFactCleanup<PlatformDbContext>("platform", SettingCommittedV1.Name,
                configuration.GetSection("Platform:AuditDelivery:Cleanup").Get<CommittedFactCleanupOptions>());
        }
        else
        {
            throw new InvalidOperationException("Platform:Storage:Provider 仅支持 Postgres / Memory。");
        }
        return services;
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/platform/audit-capacity", async ([FromKeyedServices("platform")] ICommittedFactCapacityReader reader,
            ApiResponses responses, CancellationToken token) =>
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequireAuthorization().AddEndpointFilter<NexusStackAuthorizationFilter>()
            .RequirePermission("/api/platform/audit-capacity", "GET")
            .Produces<ApiResponse<CommittedFactCapacitySnapshot>>().ProducesApiErrors(401, 403, 503);

        // 设置可能包含受限元数据；读取与管理都必须显式授权并检查当前会话。
        var settings = endpoints.MapGroup("/api/platform/settings").RequireAuthorization().ProducesApiErrors(400, 401, 403, 409, 500, 503);
        settings.AddEndpointFilter<NexusStackAuthorizationFilter>();

        // 读一个配置值。**键不合法与键没配过是两件事**：前者 400，后者 200 + value=null。
        settings.MapGet("/{key}", async (string key, SettingStore store, IClock clock, ApiResponses responses, CancellationToken cancellationToken) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            var setting = await store.GetAsync(parsed.Value, cancellationToken);
            return responses.Ok(new SettingResponse(parsed.Value.Value, parsed.Value.Scope, setting?.Value, clock.UtcNow,
                setting?.Version ?? 0, setting?.Description, setting is null ? null : EntityAuditMetadata.From(setting)));
        }).Produces<ApiResponse<SettingResponse>>().RequirePermission("/api/platform/settings/{key}", "GET");

        // 分页列出一个分组下的配置。**按段比较，不做前缀匹配**。
        settings.MapGet("/", async (ApiResponses responses, string scope, SettingStore store, [AsParameters] ApiPageRequest paging, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return Failure(new Error("platform.scope.empty", "必须给出 scope。"));
            }

            if (!paging.IsValid)
            {
                return Failure(new Error(ApiPageRequest.InvalidErrorCode, ApiPageRequest.InvalidErrorMessage));
            }

            var found = await store.ListByScopeAsync(scope, cancellationToken);

            return responses.Page(found.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal)
                .Skip((int)Math.Min(paging.Offset, found.Count)).Take(paging.Limit)
                .Select(static setting => new SettingItem(setting.Key.Value, setting.Key.Name, setting.Value, setting.Description, setting.Version, EntityAuditMetadata.From(setting))).ToArray(), found.Count, paging);
        }).Produces<ApiPage<SettingItem>>().RequirePermission("/api/platform/settings", "GET");

        // 写一个配置值。键不存在就创建——调用方不需要先问"注册过没有"（那之间有竞态）。
        settings.MapPut("/{key}", async (
            string key,
            WriteSettingRequest request,
            SettingStore store, CancellationToken cancellationToken) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            var written = await store.WriteAsync(parsed.Value, request.Value, request.Description, request.ExpectedVersion, cancellationToken);

            return written.IsFailure ? Failure(written.Error) : Results.NoContent();
        }).ProducesApiErrors(415, 503).Produces(204).RequirePermission("/api/platform/settings/{key}", "PUT");

        // 清空一个配置值。**不删除配置项本身**——"没有值"与"没注册过"是不同的状态。
        settings.MapDelete("/{key}", async (string key, long? expectedVersion, SettingStore store, CancellationToken cancellationToken) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            var cleared = await store.WriteAsync(parsed.Value, value: null, cancellationToken: cancellationToken, expectedVersion: expectedVersion);

            return cleared.IsFailure ? Failure(cleared.Error) : Results.NoContent();
        }).ProducesApiErrors(503).Produces(204).RequirePermission("/api/platform/settings/{key}", "DELETE");

        var deliveries = endpoints.MapGroup("/api/platform/audit-deliveries").RequireAuthorization()
            .ProducesApiErrors(400, 401, 403, 409, 500, 503);
        deliveries.AddEndpointFilter<NexusStackAuthorizationFilter>();
        deliveries.MapGet("/", async (ISettingAuditDelivery delivery, ApiResponses responses, CancellationToken token,
            string state = "Pending", int limit = 50) =>
        {
            if (state is not ("Pending" or "Delivered" or "DeadLettered") || limit is < 1 or > 100)
            {
                return Failure(new Error("platform.delivery_query.invalid", "投递状态必须为 Pending、Delivered 或 DeadLettered，limit 必须在 1 到 100。"));
            }
            return (IResult)responses.Ok(await delivery.ListAsync(state, limit, token).ConfigureAwait(false));
        }).RequirePermission("/api/platform/audit-deliveries", "GET").Produces<ApiResponse<IReadOnlyList<SettingAuditDelivery>>>();
        deliveries.MapPost("/{messageId:guid}/retry", async (Guid messageId, RetryAuditDeliveryRequest request,
            ISettingAuditDelivery delivery, ApiResponses responses, CancellationToken token) =>
        {
            var result = await delivery.RetryAsync(messageId, request.ExpectedDeadLetteredAt, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/platform/audit-deliveries/{messageId}/retry", "POST")
            .Produces<ApiResponse<SettingAuditDelivery>>().ProducesApiErrors(415);
        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para><b>刻意不与其它模块共用</b>：Identity 的 <c>not_found</c> 与 Files 的
    /// <c>not_found</c> 未必同义；Platform 的输入错误是 400，版本或稳定键竞争是 409。
    /// 统一它们等于用一个共用函数锁死几个模块的 HTTP 语义。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code == SettingStore.AuditCapacityExhausted.Code || error.Code == CommittedFactCapacityErrors.Unavailable.Code
            || error.Code == CommittedFactCapacityErrors.Busy.Code ? StatusCodes.Status503ServiceUnavailable
            : error.Code == SettingStore.Conflict.Code || error.Code == SettingAuditDelivery.Conflict.Code ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>写入一个配置值。</summary>
/// <param name="Value">新值；<c>null</c> 表示清空。</param>
/// <param name="Description">说明。</param>
/// <param name="ExpectedVersion">条件写版本；省略时执行无客户端版本条件的赋值。</param>
internal sealed record WriteSettingRequest(string? Value, string? Description, long? ExpectedVersion = null);

internal sealed record SettingResponse(string Key, string Scope, string? Value, DateTimeOffset At, long Version, string? Description, EntityAuditMetadata? Audit);
internal sealed record SettingItem(string Key, string Name, string? Value, string? Description, long Version, EntityAuditMetadata? Audit);
internal sealed record RetryAuditDeliveryRequest(DateTimeOffset ExpectedDeadLetteredAt);
