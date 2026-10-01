using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Platform.Application;
using NexusStackNext.Platform.Domain.Settings;
using NexusStackNext.Platform.Infrastructure;

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
    /// <returns>同一个服务集合，便于串联。</returns>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPlatformInMemoryStorage();
        return services;
    }

    /// <summary>映射本模块的端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <returns>同一个构建器，便于串联。</returns>
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // **写路径在进程内也要求认证，读路径显式声明公开。**
        //
        // 与边缘的 `platform-read`（公开、且只放 GET）一致：读是公开的，写要令牌。
        // 此前这四条在进程内**没有任何授权判定**——"边缘是唯一入口"是**编排**的事实，
        // 不是代码的事实；谁直连到这个进程谁就绕过了它。
        // 用 `RequireAuthorization()` 而不是 Identity 那个过滤器：那个要算**权限键**，
        // 是 RBAC 的落点；Platform 还没有登记权限键，它要的只是"令牌有效"。
        var settings = endpoints.MapGroup("/api/platform/settings").RequireAuthorization().ProducesApiErrors(400, 401, 403, 500);

        // 读一个配置值。**键不合法与键没配过是两件事**：前者 400，后者 200 + value=null。
        settings.MapGet("/{key}", async (string key, SettingStore store, IClock clock, ApiResponses responses) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            return responses.Ok(new SettingResponse(parsed.Value.Value, parsed.Value.Scope, await store.ReadAsync(parsed.Value), clock.UtcNow));
        }).Produces<ApiResponse<SettingResponse>>().AllowAnonymous();

        // 分页列出一个分组下的配置。**按段比较，不做前缀匹配**。
        settings.MapGet("/", async (ApiResponses responses, string scope, SettingStore store, [AsParameters] ApiPageRequest paging) =>
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return Failure(new Error("platform.scope.empty", "必须给出 scope。"));
            }

            if (!paging.IsValid)
            {
                return Failure(new Error(ApiPageRequest.InvalidErrorCode, ApiPageRequest.InvalidErrorMessage));
            }

            var found = await store.ListByScopeAsync(scope);

            return responses.Page(found.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal)
                .Skip((int)Math.Min(paging.Offset, found.Count)).Take(paging.Limit)
                .Select(static setting => new SettingItem(setting.Key.Value, setting.Key.Name, setting.Value, setting.Description)).ToArray(), found.Count, paging);
        }).Produces<ApiPage<SettingItem>>().AllowAnonymous();

        // 写一个配置值。键不存在就创建——调用方不需要先问"注册过没有"（那之间有竞态）。
        settings.MapPut("/{key}", async (
            string key,
            WriteSettingRequest request,
            SettingStore store) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            var written = await store.WriteAsync(parsed.Value, request.Value, request.Description);

            return written.IsFailure ? Failure(written.Error) : Results.NoContent();
        }).ProducesApiErrors(415).Produces(204);

        // 清空一个配置值。**不删除配置项本身**——"没有值"与"没注册过"是不同的状态。
        settings.MapDelete("/{key}", async (string key, SettingStore store) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            var cleared = await store.WriteAsync(parsed.Value, value: null);

            return cleared.IsFailure ? Failure(cleared.Error) : Results.NoContent();
        }).Produces(204);

        return endpoints;
    }

    /// <summary>
    /// 本模块自己的错误码 → 状态码映射。
    /// <para><b>刻意不与其它模块共用</b>：Identity 的 <c>not_found</c> 与 Files 的
    /// <c>not_found</c> 未必同义（Files 的是 404，Platform 的全部是 400）。
    /// 统一它们等于用一个共用函数锁死几个模块的 HTTP 语义。</para>
    /// </summary>
    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>写入一个配置值。</summary>
/// <param name="Value">新值；<c>null</c> 表示清空。</param>
/// <param name="Description">说明。</param>
internal sealed record WriteSettingRequest(string? Value, string? Description);

internal sealed record SettingResponse(string Key, string Scope, string? Value, DateTimeOffset At);
internal sealed record SettingItem(string Key, string Name, string? Value, string? Description);
