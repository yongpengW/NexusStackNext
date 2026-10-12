using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.Auditing.Contracts;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Web;
using NexusStackNext.Notifications.Application;
using NexusStackNext.Notifications.Infrastructure;

namespace NexusStackNext.Notifications.Endpoints;

/// <summary>可选的持久邮件模块，由宿主显式启用。</summary>
public static class NotificationsModule
{
    /// <summary>注册邮件用例、启动检查与发送循环。</summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var smtp = configuration.GetSection("Notifications:Smtp").Get<SmtpOptions>() ?? new();
        if (smtp.Security == MailKit.Security.SecureSocketOptions.None && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        { throw new InvalidOperationException("生产 SMTP 必须显式启用 StartTls 或 SslOnConnect。"); }
        var connection = configuration.GetConnectionString("Notifications");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException("启用 Notifications 必须配置 ConnectionStrings:Notifications。"); }
        services.AddNotificationsPostgres(connection, smtp);
        if (configuration.GetValue("Notifications:Worker:Enabled", true)) { services.AddHostedService<EmailWorker>(); }
        return services;
    }

    /// <summary>映射受当前会话、独立操作权限和本人归属约束的邮件入口。</summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/notifications/emails").RequireAuthorization().RequireAuthenticated()
            .ProducesApiErrors(400, 401, 403, 404, 409, 500, 503);
        group.AddEndpointFilter<NexusStackAuthorizationFilter>();
        group.MapPost("", async (SubmitEmail command, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(command, token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Accepted(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/notifications/emails", "POST")
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024), new OperationDescription("notifications.email.submit", "登记邮件发送"))
            .Produces<ApiResponse<EmailStatus>>(202).ProducesApiErrors(413, 415);
        group.MapGet("/{id:guid}", async (Guid id, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.QueryAsync(new GetEmail(id), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/notifications/emails/{id}", "GET").Produces<ApiResponse<EmailStatus>>();
        group.MapPost("/{id:guid}/resolve", async (Guid id, EmailResolution request, ISender sender, ApiResponses responses, CancellationToken token) =>
        {
            var result = await sender.SendAsync(new ResolveEmail(id, request.ExpectedAttempt, request.Decision), token).ConfigureAwait(false);
            return result.IsSuccess ? (IResult)responses.Ok(result.Value) : Failure(result.Error);
        }).RequirePermission("/api/notifications/emails/{id}/resolve", "POST")
            .WithMetadata(new OperationDescription("notifications.email.resolve", "明确处置邮件发送结果",
                new OperationSubjectRoute("EmailDelivery", "id", OperationSubjectIdKind.Uuid)))
            .Produces<ApiResponse<EmailStatus>>();
        return endpoints;
    }

    private static IResult Failure(Error error) => Results.Problem(title: error.Message, statusCode: error.Code switch
    {
        "notifications.not_found" => StatusCodes.Status404NotFound,
        "notifications.unauthorized" => StatusCodes.Status401Unauthorized,
        "notifications.conflict" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    }, extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
}

internal sealed record EmailResolution(int ExpectedAttempt, string Decision);

internal sealed partial class EmailWorker(IServiceScopeFactory scopes, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if ((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new DispatchPendingEmail(), stoppingToken).ConfigureAwait(false)).Value > 0) { continue; }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // 后台边界继续处理其他意图；异常自由文本可能包含地址、配置或 SMTP 回复。
                ExecutionFailed(logger);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(1, LogLevel.Error, "SMTP 邮件执行未完成；通过持久邮件状态调查，不自动重发 Unknown。")]
    private static partial void ExecutionFailed(ILogger logger);
}
