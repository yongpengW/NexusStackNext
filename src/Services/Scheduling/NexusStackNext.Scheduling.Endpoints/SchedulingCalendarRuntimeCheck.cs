using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexusStackNext.Scheduling.Application;

namespace NexusStackNext.Scheduling.Endpoints;

internal sealed class SchedulingCalendarRuntimeCheck(IScheduleCalendar calendar) : IHostedService, IHealthCheck
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Probe().Status != HealthStatus.Healthy)
        {
            throw new InvalidOperationException("scheduling.timezone.unavailable：Scheduling 需要可用的 IANA 时区数据；检查 OS/ICU/tzdata 与全球化配置。");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Probe());
    }

    private HealthCheckResult Probe()
    {
        // UTC、常规偏移、一小时 DST、半小时 DST；不把任一计划故障升级为整个宿主不就绪。
        foreach (var zone in new[] { "Etc/UTC", "Asia/Shanghai", "America/New_York", "Australia/Lord_Howe" })
        {
            var result = calendar.Preview(new ScheduleRuleInput("Cron", "0 0 * * *", zone),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1);
            if (result.IsFailure) { return HealthCheckResult.Unhealthy("scheduling.timezone.unavailable"); }
        }
        return HealthCheckResult.Healthy();
    }
}
