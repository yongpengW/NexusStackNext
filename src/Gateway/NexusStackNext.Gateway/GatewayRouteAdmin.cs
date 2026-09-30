using Microsoft.AspNetCore.Authorization;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Gateway.Routing;
using Yarp.ReverseProxy.Configuration;

namespace NexusStackNext.Gateway;

/// <summary>
/// 路由表的管理 API（票据 12）。
///
/// <para><b>它必须鉴权，而且只认根管理员。</b>改一张路由表是**系统级**的动作——
/// 加一条路由等于把某个上游暴露到边缘上。参照仓库的这套 API 因为认证方案没注册而
/// **必然失败**（<c>ApiControllerBase.cs:16</c> 硬编码了一个只在别的分支注册的方案），
/// 也就是说那段代码从来没有真正工作过。</para>
///
/// <para><b>这里不做业务授权</b>（ADR-0003：网关验签、上下文授权）——但"谁能改路由表"
/// 不是业务授权，它是网关**自己的**管理边界。用一个要求根管理员的策略来表达，
/// 而不是假装它属于某个业务上下文。</para>
///
/// <para><b>写入顺序是"先校验、再落盘、最后生效"。</b>反过来的话，一次落盘失败会留下
/// "运行中的路由表与磁盘上的不一致"——而那种不一致要到重启时才出现，
/// 且没有任何线索指向当时那次失败的写入。</para>
/// </summary>
public static class GatewayRouteAdmin
{
    /// <summary>只有根管理员能改路由表的策略名。</summary>
    public const string RootOnlyPolicy = "gateway-route-admin";

    /// <summary>映射路由表管理端点。</summary>
    /// <param name="app">应用。</param>
    /// <returns>同一个应用，便于串联。</returns>
    public static WebApplication MapGatewayRouteAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/gateway/routes").RequireAuthorization(RootOnlyPolicy);

        // 处理器写成**具名静态方法**而不是内联 lambda：内联版本在
        // "同时返回 IResult 与 await 一个 Task<IResult>" 时，编译器推断不出委托类型
        // （CS4010，报的是 `Task<?>`，很难从错误信息看出原因）。
        // 具名方法的返回类型是写下来的，推断问题就不存在了。
        admin.MapGet("/{routeId}", GetRouteAsync);
        admin.MapPost("/", AddRouteAsync);
        admin.MapPut("/{routeId}", UpdateRouteAsync);
        admin.MapDelete("/{routeId}", DeleteRouteAsync);

        return app;
    }

    private static async Task<IResult> GetRouteAsync(
        string routeId,
        IRouteTableStore store,
        CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return BadRequest(loaded.Error);
        }

        var route = FindRoute(loaded.Value, routeId);

        return route is null ? NotFound(routeId) : Results.Ok(route);
    }

    private static async Task<IResult> AddRouteAsync(
        RouteDefinition route,
        IRouteTableStore store,
        RouteTableReloader reloader,
        CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return BadRequest(loaded.Error);
        }

        if (FindRoute(loaded.Value, route.RouteId) is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: $"路由已存在：{route.RouteId}。");
        }

        return await SaveAndApplyAsync(
            store,
            reloader,
            GatewayRouteTable.Create([.. loaded.Value.Routes, route], loaded.Value.Clusters),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> UpdateRouteAsync(
        string routeId,
        RouteDefinition route,
        IRouteTableStore store,
        RouteTableReloader reloader,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(routeId, route.RouteId, StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "路径里的标识与请求体里的不一致。");
        }

        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return BadRequest(loaded.Error);
        }

        var index = IndexOfRoute(loaded.Value, routeId);
        if (index < 0)
        {
            return NotFound(routeId);
        }

        var routes = loaded.Value.Routes.ToList();
        routes[index] = route;

        return await SaveAndApplyAsync(
            store,
            reloader,
            GatewayRouteTable.Create(routes, loaded.Value.Clusters),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteRouteAsync(
        string routeId,
        IRouteTableStore store,
        RouteTableReloader reloader,
        CancellationToken cancellationToken)
    {
        var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return BadRequest(loaded.Error);
        }

        var index = IndexOfRoute(loaded.Value, routeId);
        if (index < 0)
        {
            return NotFound(routeId);
        }

        var routes = loaded.Value.Routes.ToList();
        routes.RemoveAt(index);

        return await SaveAndApplyAsync(
            store,
            reloader,
            GatewayRouteTable.Create(routes, loaded.Value.Clusters),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验 → 落盘 → 生效。任何一步失败都停下。</summary>
    private static async Task<IResult> SaveAndApplyAsync(
        IRouteTableStore store,
        RouteTableReloader reloader,
        Result<GatewayRouteTable> candidate,
        CancellationToken cancellationToken)
    {
        // 校验：一张无效的路由表不该被写进磁盘。`Create` 会跑完整套
        // `RouteTableValidator`（重复标识、悬空集群引用、限流策略名……）。
        if (candidate.IsFailure)
        {
            return BadRequest(candidate.Error);
        }

        var saved = await store.SaveAsync(candidate.Value, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return BadRequest(saved.Error);
        }

        reloader.Apply(candidate.Value);

        return Results.NoContent();
    }

    private static RouteDefinition? FindRoute(GatewayRouteTable table, string routeId) =>
        table.Routes.FirstOrDefault(candidate => string.Equals(candidate.RouteId, routeId, StringComparison.Ordinal));

    private static int IndexOfRoute(GatewayRouteTable table, string routeId)
    {
        for (var i = 0; i < table.Routes.Count; i++)
        {
            if (string.Equals(table.Routes[i].RouteId, routeId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static IResult NotFound(string routeId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"路由不存在：{routeId}。");

    private static IResult BadRequest(Error error) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message);
}

/// <summary>
/// 把新的路由表推给 YARP，**不需要重启**。
///
/// <para><b>没有它，"保存成功"就是一句谎话。</b>网关原先在启动时一次性
/// <c>LoadFromMemory</c>，之后改磁盘上的路由表对运行中的进程毫无影响——
/// 于是管理 API 会返回 204，而流量照旧走老规则。
/// 那正是本仓反复记录的那种失效：<b>接口说成功，事实没变。</b></para>
/// </summary>
public sealed class RouteTableReloader(InMemoryConfigProvider provider)
{
    private readonly InMemoryConfigProvider _provider = provider;

    /// <summary>把一张路由表推给 YARP。</summary>
    /// <param name="table">路由表。</param>
    public void Apply(GatewayRouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var (routes, clusters) = YarpConfigMapper.ToYarp(table);
        _provider.Update(routes, clusters);
    }
}

/// <summary>网关自己的授权策略。</summary>
public static class GatewayAuthorizationPolicies
{
    /// <summary>声明"只有根管理员"的策略。</summary>
    /// <param name="options">授权选项。</param>
    public static void AddGatewayPolicies(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(GatewayRouteAdmin.RootOnlyPolicy, policy => policy
            .RequireAuthenticatedUser()
            // 声明名与签发方共用同一个常量——写错一个字符的后果是
            // "任何人都改不了路由表"，而它看起来像权限配置问题，不像拼写问题。
            .RequireClaim(NexusStackClaims.Root, "true"));
    }
}
