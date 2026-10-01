using Microsoft.AspNetCore.Authorization;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Gateway.Routing;

namespace NexusStackNext.Gateway;

/// <summary>路由管理是网关自己的管理边界，仅根管理员可访问。</summary>
public static class GatewayRouteAdmin
{
    /// <summary>根管理员授权策略名。</summary>
    public const string RootOnlyPolicy = "gateway-route-admin";

    /// <summary>映射路由表管理端点。</summary>
    /// <param name="app">应用。</param>
    /// <returns>同一个应用。</returns>
    public static WebApplication MapGatewayRouteAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var admin = app.MapGroup("/gateway/routes").RequireAuthorization(RootOnlyPolicy).ProducesApiErrors(400, 401, 403, 500);
        admin.MapGet("/{routeId}", (string routeId, GatewayRouteConfiguration configuration, ApiResponses responses) =>
            FindRoute(configuration.Current, routeId) is { } route ? responses.Ok(route) : Failure(MissingRoute(routeId)))
            .Produces<ApiResponse<RouteDefinition>>().ProducesApiErrors(404);
        admin.MapPost("/", AddRouteAsync).Produces(204).ProducesApiErrors(409, 503);
        admin.MapPut("/{routeId}", UpdateRouteAsync).Produces(204).ProducesApiErrors(404, 503);
        admin.MapDelete("/{routeId}", DeleteRouteAsync).Produces(204).ProducesApiErrors(404, 503);
        return app;
    }

    private static async Task<IResult> AddRouteAsync(
        RouteDefinition route, GatewayRouteConfiguration configuration, CancellationToken cancellationToken)
    {
        var result = await configuration.UpdateAsync(table => FindRoute(table, route.RouteId) is not null
            ? Result.Failure<GatewayRouteTable>(new Error("gateway.route.exists", $"路由已存在：{route.RouteId}。"))
            : GatewayRouteTable.Create([.. table.Routes, route], table.Clusters), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
    }

    private static async Task<IResult> UpdateRouteAsync(
        string routeId, RouteDefinition route, GatewayRouteConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!string.Equals(routeId, route.RouteId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(new Error("gateway.route.id_mismatch", "路径里的标识与请求体里的不一致。"));
        }

        var result = await configuration.UpdateAsync(table => FindRoute(table, routeId) is null
            ? Result.Failure<GatewayRouteTable>(MissingRoute(routeId))
            : GatewayRouteTable.Create(table.Routes.Select(item => string.Equals(item.RouteId, routeId, StringComparison.OrdinalIgnoreCase) ? route : item), table.Clusters),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
    }

    private static async Task<IResult> DeleteRouteAsync(
        string routeId, GatewayRouteConfiguration configuration, CancellationToken cancellationToken)
    {
        var result = await configuration.UpdateAsync(table => FindRoute(table, routeId) is null
            ? Result.Failure<GatewayRouteTable>(MissingRoute(routeId))
            : GatewayRouteTable.Create(table.Routes.Where(item => !string.Equals(item.RouteId, routeId, StringComparison.OrdinalIgnoreCase)), table.Clusters),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Failure(result.Error);
    }

    private static RouteDefinition? FindRoute(GatewayRouteTable table, string routeId) =>
        table.Routes.FirstOrDefault(route => string.Equals(route.RouteId, routeId, StringComparison.OrdinalIgnoreCase));

    private static Error MissingRoute(string routeId) => new("gateway.route.missing", $"路由不存在：{routeId}。");

    private static IResult Failure(Error error) => Results.Problem(
        title: error.Message,
        statusCode: error.Code switch
        {
            "gateway.route.exists" => StatusCodes.Status409Conflict,
            "gateway.route.missing" => StatusCodes.Status404NotFound,
            "gateway.route_table.write_failed" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        },
        extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

/// <summary>网关自己的授权策略。</summary>
public static class GatewayAuthorizationPolicies
{
    /// <summary>声明根管理员策略。</summary>
    /// <param name="options">授权选项。</param>
    public static void AddGatewayPolicies(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(GatewayRouteAdmin.RootOnlyPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NexusStackClaims.Root, "true"));
    }
}
