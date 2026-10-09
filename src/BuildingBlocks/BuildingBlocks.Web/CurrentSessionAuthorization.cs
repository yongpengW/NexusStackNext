using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Authorization;
using NexusStackNext.BuildingBlocks.Application.Security;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>当前会话必须有效且具备权威根身份；只用于现阶段根操作者管理面。</summary>
public sealed class CurrentRootSessionRequirement : IAuthorizationRequirement;

/// <summary>使用端点声明的操作键，在绑定业务正文之前取得权威许可。</summary>
public sealed class CurrentOperationRequirement : IAuthorizationRequirement;

internal sealed class CurrentOperationHandler(IRequestAccessValidator access, ICurrentUser user) : AuthorizationHandler<CurrentOperationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentOperationRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http) { context.Fail(); return; }
        if (user.UserId is null) { http.Items[CurrentRootSessionHandler.FailureKey] = StatusCodes.Status401Unauthorized; context.Fail(); return; }
        var operation = http.GetEndpoint()?.Metadata.GetMetadata<AuthorizationRequirement>();
        if (operation is not { Mode: AuthorizationMode.PermissionKey, PermissionKey: { } key }) { context.Fail(); return; }
        var decision = await access.ValidateAsync(user.UserId, user.SessionVersion, key, http.RequestAborted).ConfigureAwait(false);
        if (decision.IsFailure)
        {
            http.Items[CurrentRootSessionHandler.FailureKey] = decision.Error == SessionValidationErrors.Invalid ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable;
            context.Fail();
        }
        else if (decision.Value) { context.Succeed(requirement); }
        else { context.Fail(); }
    }
}

internal sealed class CurrentRootSessionHandler(ISessionValidator sessions, ICurrentUser user) : AuthorizationHandler<CurrentRootSessionRequirement>
{
    internal static readonly object FailureKey = new();

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentRootSessionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http)
        { context.Fail(); return; }
        if (user.UserId is null)
        {
            http.Items[FailureKey] = StatusCodes.Status401Unauthorized;
            context.Fail();
            return;
        }
        var session = await sessions.ValidateAsync(user.UserId, user.SessionVersion, http.RequestAborted).ConfigureAwait(false);
        if (session.IsFailure)
        {
            http.Items[FailureKey] = session.Error == SessionValidationErrors.Invalid ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable;
            context.Fail();
        }
        else if (session.Value.IsRoot) { context.Succeed(requirement); }
        else { context.Fail(); }
    }
}

internal sealed class CurrentSessionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && context.Items[CurrentRootSessionHandler.FailureKey] is int status)
        {
            var error = status == StatusCodes.Status401Unauthorized ? SessionValidationErrors.Invalid : SessionValidationErrors.Unavailable;
            if (status == StatusCodes.Status401Unauthorized) { context.Response.Headers.WWWAuthenticate = "Bearer"; }
            return Results.Problem(statusCode: status, title: error.Message,
                extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code }).ExecuteAsync(context);
        }
        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>宿主显式装配当前根身份与业务操作授权；权限模式由端点声明。</summary>
public static class CurrentSessionAuthorizationServices
{
    /// <summary>在端点执行及请求体绑定之前拒绝无效会话。</summary>
    /// <param name="services">宿主容器。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddCurrentSessionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuthorizationHandler, CurrentRootSessionHandler>();
        services.AddScoped<IAuthorizationHandler, CurrentOperationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, CurrentSessionAuthorizationResultHandler>();
        return services;
    }
}
