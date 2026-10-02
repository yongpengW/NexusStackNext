using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NexusStackNext.Files.Infrastructure.Persistence;

internal sealed class FilesDatabaseHealthCheck(FilesDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            _ = await context.Files.AnyAsync(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Files 数据库不可用。");
        }
    }
}
