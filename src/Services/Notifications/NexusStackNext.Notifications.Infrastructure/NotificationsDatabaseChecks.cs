using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.Notifications.Infrastructure;

internal sealed class NotificationsStartupCheck(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        try
        {
            if ((await database.Database.GetPendingMigrationsAsync(timeout.Token).ConfigureAwait(false)).Any())
            { throw new InvalidOperationException("Notifications 数据库需要迁移；先执行 migrate-notifications。"); }
            _ = await database.Emails.AnyAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DbException or ArgumentException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Notifications 数据库不可用；检查连接配置与迁移。"); }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NotificationsHealthCheck(NotificationsDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            _ = await database.Emails.AnyAsync(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is DbException or InvalidOperationException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Notifications 数据库不可用。"); }
    }
}
