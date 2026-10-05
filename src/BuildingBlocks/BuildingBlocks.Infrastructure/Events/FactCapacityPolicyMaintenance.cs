using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>策略凭据维护的有限调度；七天凭据期和二十四小时确认期不能由配置缩短。</summary>
public sealed record FactCapacityPolicyMaintenanceOptions
{
    /// <summary>是否运行后台维护，默认开启。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>每轮最多释放一百个请求，允许一至一千。</summary>
    public int BatchSize { get; init; } = 100;
    /// <summary>轮次间隔，默认一分钟，允许一秒至一小时。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>单轮独立预算，默认三秒，允许五十毫秒至三十秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        if (BatchSize is < 1 or > 1000 || Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromHours(1)
            || Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("容量策略凭据维护配置超出允许范围。");
        }
    }
}

/// <summary>为各模块的所属清理端口接入有界调度与脱敏诊断。</summary>
public static class FactCapacityPolicyMaintenance
{
    /// <summary>注册有界策略凭据维护；不复用业务副本的删除计数。</summary>
    /// <param name="services">宿主服务。</param>
    /// <param name="contextKey">模块代码声明的所属清理端口键。</param>
    /// <param name="options">本模块维护调度。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddCommittedFactPolicyMaintenance(this IServiceCollection services,
        string contextKey, FactCapacityPolicyMaintenanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextKey);
        if (contextKey.Length is < 1 or > 63 || contextKey[0] is < 'a' or > 'z'
            || contextKey.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new ArgumentException("维护来源必须是模块声明的安全标识。", nameof(contextKey));
        }
        var policy = options ?? new();
        policy.Validate();
        var status = new FactCapacityPolicyMaintenanceStatus(policy.Enabled);
        services.AddHealthChecks().AddCheck(contextKey + "-policy-cleanup", status.Read, tags: ["auditing-diagnostics"]);
        if (policy.Enabled)
        {
            services.AddSingleton<IHostedService>(provider => new FactCapacityPolicyMaintenanceWorker(
                provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IClock>(), contextKey, policy, status,
                provider.GetRequiredService<ILogger<FactCapacityPolicyMaintenanceWorker>>()));
        }
        return services;
    }
}

internal sealed partial class FactCapacityPolicyMaintenanceWorker(IServiceScopeFactory scopes, IClock clock,
    string contextKey, FactCapacityPolicyMaintenanceOptions options, FactCapacityPolicyMaintenanceStatus status,
    ILogger<FactCapacityPolicyMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            budget.CancelAfter(options.Timeout);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCapacityPolicyCleanup>(contextKey);
                var released = await cleanup.CleanupAsync(options.BatchSize, clock.UtcNow, budget.Token).ConfigureAwait(false);
                status.Succeeded(released);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                status.Failed();
                try { LogCleanupFailed(contextKey); }
                catch (Exception) { /* 诊断故障不停止下一轮，也不输出原始异常或递归采集。 */ }
            }
            try { await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "{Context} 容量策略凭据清理失败，将在下一轮重试。")]
    private partial void LogCleanupFailed(string context);
}

internal sealed class FactCapacityPolicyMaintenanceStatus(bool enabled)
{
    private readonly Lock _gate = new();
    private long _failures;
    private long _runs;
    private long _released;
    private bool _degraded;

    internal void Succeeded(int released) { lock (_gate) { _degraded = false; _runs++; _released += released; } }
    internal void Failed() { lock (_gate) { _failures++; _degraded = true; } }

    internal HealthCheckResult Read()
    {
        lock (_gate)
        {
            var data = new Dictionary<string, object>
            {
                ["enabled"] = enabled,
                ["cleanupFailures"] = _failures,
                ["cleanupDegraded"] = _degraded,
                ["cleanupRuns"] = _runs,
                ["releasedRequests"] = _released,
            };
            return _degraded ? HealthCheckResult.Degraded("容量策略凭据清理失败，将在下一轮重试。", data: data)
                : HealthCheckResult.Healthy(data: data);
        }
    }
}
