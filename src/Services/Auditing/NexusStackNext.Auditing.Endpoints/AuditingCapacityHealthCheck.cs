using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Auditing.Application;

namespace NexusStackNext.Auditing.Endpoints;

internal sealed class AuditingCapacityHealthCheck(IAuditStorageCapacityReader capacity) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await capacity.ReadAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["factsRecords"] = snapshot.Facts.Records,
                ["factsInstanceLimit"] = snapshot.Facts.InstanceLimit,
                ["observationsRecords"] = snapshot.Observations.Records,
                ["observationsInstanceLimit"] = snapshot.Observations.InstanceLimit,
            };
            return snapshot.Facts.Available == 0 || snapshot.Observations.Available == 0
                ? HealthCheckResult.Degraded("中央审计接纳容量已满，已有证据仍保留。", data: data)
                : HealthCheckResult.Healthy(data: data);
        }
        catch (AuditStorageUnavailableException) { return HealthCheckResult.Unhealthy("中央审计容量诊断暂不可用。"); }
    }
}
