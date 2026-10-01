using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NexusStackNext.Identity.Infrastructure.Persistence;

/// <summary>Identity 就绪探针；运行期依赖失败不会改变进程存活判定。</summary>
internal sealed class IdentityDatabaseHealthCheck(IdentityDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await context.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Identity 数据库不可用。");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Identity 数据库检查超时。");
        }
    }
}
