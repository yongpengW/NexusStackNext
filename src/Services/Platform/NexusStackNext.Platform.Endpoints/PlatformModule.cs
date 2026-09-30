using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
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

        var settings = endpoints.MapGroup("/api/platform/settings");

        // 读一个配置值。**键不合法与键没配过是两件事**：前者 400，后者 200 + value=null。
        settings.MapGet("/{key}", async (string key, SettingStore store, IClock clock) =>
        {
            var parsed = SettingKey.Create(key);
            if (parsed.IsFailure)
            {
                return Failure(parsed.Error);
            }

            return Results.Ok(new
            {
                key = parsed.Value.Value,
                scope = parsed.Value.Scope,
                value = await store.ReadAsync(parsed.Value),
                at = clock.UtcNow,
            });
        });

        // 列出一个分组下的全部配置。**按段比较，不做前缀匹配**。
        settings.MapGet("/", async (string scope, SettingStore store) =>
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return Failure(new Error("platform.scope.empty", "必须给出 scope。"));
            }

            var found = await store.ListByScopeAsync(scope);

            return Results.Ok(new
            {
                scope,
                items = found.Select(static setting => new
                {
                    key = setting.Key.Value,
                    name = setting.Key.Name,
                    setting.Value,
                    setting.Description,
                }),
            });
        });

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
        });

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
        });

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
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}

/// <summary>写入一个配置值。</summary>
/// <param name="Value">新值；<c>null</c> 表示清空。</param>
/// <param name="Description">说明。</param>
internal sealed record WriteSettingRequest(string? Value, string? Description);
