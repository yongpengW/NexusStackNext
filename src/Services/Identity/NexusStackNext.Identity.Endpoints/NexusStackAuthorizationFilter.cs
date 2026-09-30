using Microsoft.AspNetCore.Authorization;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain.Authorization;

namespace NexusStackNext.Identity.Endpoints;

/// <remarks>
/// <para><b>它为什么住在 Identity.Endpoints 而不是某个共享工程。</b>第一版我把它放进了
/// <c>BuildingBlocks.AspNetCore</c>，理由是"五个上下文都会用到"——而当时只有**一个**在用。
/// 架构不变量测试立刻拦下了它（不变量 7：被第二个消费者证明需要才允许上移）。</para>
///
/// <para><b>它拦得对。</b>那是一次**推测**的上移：部署约定确实要求所有上下文都授权，
/// 但"要求"不等于"已经需要"。等第二个上下文真的要挂过滤器时再上移，
/// 那时它的形状会被第二个使用者修正一次——而那正是共享内核该长出来的方式。</para>
/// </remarks>
/// <summary>一个端点要求的授权方式。</summary>
/// <param name="Mode">四档授权模式。</param>
/// <param name="PermissionKey">需要哪个权限键；只在 <see cref="AuthorizationMode.PermissionKey"/> 下有值。</param>
public sealed record AuthorizationRequirement(AuthorizationMode Mode, PermissionKey? PermissionKey = null);

/// <summary>
/// 请求授权过滤器：把预计算的权限集合落到**每个上下文自己的**请求管线上（票据 11）。
///
/// <para><b>认证与授权分离</b>（ADR-0003）：网关验签、上下文授权。这个过滤器只依赖
/// "已认证的身份"与一个权限检查端口——**它不解析令牌**，也不认识 JWT。</para>
///
/// <para><b>默认拒绝，而且是结构性的。</b>端点既没有 <see cref="AuthorizationRequirement"/>
/// 也没有 <see cref="IAllowAnonymous"/> 标记时，过滤器按 <see cref="AuthorizationMode.DenyAll"/> 处理。
/// 参照仓库在对应位置是 <c>ApiAuthorizationOptions.cs:21</c> 默认 <c>RootOnly</c> 且**配置化**——
/// 配置缺失时它 fail-open，而"忘了配"是常态。</para>
///
/// <para>这里**不配置化**：默认值就是最严的那一档，要放行必须有人写下那句话。
/// 于是"新加了一个端点但忘了标注"的结果是 403（看得见），而不是对所有人开放（看不见）。</para>
/// </summary>
public sealed class NexusStackAuthorizationFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var metadata = http.GetEndpoint()?.Metadata;

        // **公开必须是写下来的。** 它在最前面，因为它是唯一一个"连身份都不需要"的档。
        //
        // 用的是框架自己的 `AllowAnonymous`（`IAllowAnonymous`）而不是自造一个：
        // 第一版自造了一个，结果与框架的同名扩展方法**二义**——那说明这个标记不需要我们发明。
        if (metadata?.GetMetadata<IAllowAnonymous>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        // **既没声明要求、也没声明公开 → 拒绝。** 这是整条链上唯一一处"没写等于最严"的默认，
        // 也是有意的：忘标注的代价必须是 403，而不是对所有人开放。
        var requirement = metadata?.GetMetadata<AuthorizationRequirement>()
            ?? new AuthorizationRequirement(AuthorizationMode.DenyAll);

        var currentUser = http.RequestServices.GetService<ICurrentUser>();
        var isAuthenticated = currentUser?.UserId is not null;
        var isRoot = currentUser?.IsRoot ?? false;

        // **撤销检查排在权限之前。** 一个已被撤销的令牌不该继续走后面的判定——
        // 它连"这个身份现在还算不算数"都没过。
        if (isAuthenticated)
        {
            var sessions = http.RequestServices.GetRequiredService<ISessionVersionStore>();
            var current = sessions.Read(currentUser!.UserId!);

            // 版本对不上就是"这个访问令牌已被撤销"——**包括令牌里根本没有版本声明**，
            // 那是 fail-closed：认不出来的身份不放行。
            if (currentUser.SessionVersion != current)
            {
                return Unauthorized();
            }
        }

        if (requirement.Mode == AuthorizationMode.PermissionKey && isAuthenticated && !isRoot)
        {
            var checker = http.RequestServices.GetRequiredService<IPermissionChecker>();

            var checkedPermissions = await checker
                .ReadAsync(currentUser!.UserId!, http.RequestAborted)
                .ConfigureAwait(false);

            if (checkedPermissions.IsFailure)
            {
                // **查不出权限就是拒绝。** 一次数据库抖动不该变成一次越权。
                return Forbidden(checkedPermissions.Error.Message);
            }

            var decision = AccessPolicy.Decide(
                isAuthenticated,
                isRoot,
                checkedPermissions.Value,
                requirement.PermissionKey,
                requirement.Mode);

            return decision switch
            {
                AccessDecision.Allowed => await next(context).ConfigureAwait(false),
                AccessDecision.Unauthenticated => Unauthorized(),
                _ => Forbidden("当前身份没有访问该资源的权限。"),
            };
        }

        // 其余三档靠身份本身就够，不需要回源查权限。
        var simpleDecision = AccessPolicy.Decide(
            isAuthenticated,
            isRoot,
            granted: null,
            requirement.PermissionKey,
            requirement.Mode);

        return simpleDecision switch
        {
            AccessDecision.Allowed => await next(context).ConfigureAwait(false),
            AccessDecision.Unauthenticated => Unauthorized(),
            _ => Forbidden("当前身份没有访问该资源的权限。"),
        };
    }

    private static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "需要先认证。");

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "无权访问。", detail: detail);
}

/// <summary>把授权要求挂到端点上。</summary>
public static class AuthorizationEndpointConventionBuilderExtensions
{
    /// <summary>声明这个端点需要哪个权限键。</summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="routeTemplate">路由模板——与 <c>api_resources</c> 里登记的一致。</param>
    /// <param name="httpMethod">HTTP 方法。</param>
    /// <returns>同一个构建器。</returns>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string routeTemplate, string httpMethod)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.Metadata.Add(new AuthorizationRequirement(
            AuthorizationMode.PermissionKey,
            PermissionKey.From(routeTemplate, httpMethod))));

        return builder;
    }

    /// <summary>声明这个端点只要求"已认证"。</summary>
    /// <param name="builder">端点构建器。</param>
    /// <returns>同一个构建器。</returns>
    public static TBuilder RequireAuthenticated<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.Metadata.Add(
            new AuthorizationRequirement(AuthorizationMode.Authenticated)));

        return builder;
    }

}

/// <summary>注册请求授权。</summary>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>注册请求授权过滤器。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一个集合，便于链式调用。</returns>
    public static IServiceCollection AddNexusStackAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 过滤器依赖 `IPermissionChecker`（通常是 Scoped），所以它自己也必须是 Scoped——
        // 注册成 Singleton 会变成捕获依赖。
        services.AddScoped<NexusStackAuthorizationFilter>();

        return services;
    }
}
