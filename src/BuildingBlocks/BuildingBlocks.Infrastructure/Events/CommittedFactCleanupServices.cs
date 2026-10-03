using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Events;

/// <summary>由每个事实生产模块显式接入自己的维护循环。</summary>
public static class CommittedFactCleanupServices
{
    /// <summary>注册所属上下文的 PostgreSQL 事实副本维护。</summary>
    /// <typeparam name="TContext">所属数据库。</typeparam>
    /// <param name="services">容器。</param>
    /// <param name="owner">命名的上下文。</param>
    /// <param name="eventName">代码声明的事实事件，不接受请求指定。</param>
    /// <param name="options">维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddCommittedFactCleanup<TContext>(this IServiceCollection services, string owner,
        string eventName, CommittedFactCleanupOptions? options = null) where TContext : NexusStackDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (eventName.Length > 200) { throw new ArgumentOutOfRangeException(nameof(eventName)); }
        return services.AddCommittedFactCleanup(owner, (provider, policy) => new EfCommittedFactCleanup<TContext>(
            provider.GetRequiredService<TContext>(), eventName, policy, provider.GetRequiredService<IClock>()), options);
    }

    /// <summary>注册所属模块提供的维护适配器，复用相同的循环、预算与诊断。</summary>
    /// <param name="services">容器。</param>
    /// <param name="owner">命名上下文。</param>
    /// <param name="create">在维护作用域中构造所属适配器。</param>
    /// <param name="options">维护策略。</param>
    /// <returns>原容器。</returns>
    public static IServiceCollection AddCommittedFactCleanup(this IServiceCollection services, string owner,
        Func<IServiceProvider, CommittedFactCleanupOptions, ICommittedFactCleanup> create, CommittedFactCleanupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(create);
        var policy = options ?? new();
        policy.Validate();
        services.AddKeyedScoped<ICommittedFactCleanup>(owner, (provider, _) => create(provider, policy));
        var status = new CommittedFactCleanupStatus(policy.Enabled);
        services.AddHealthChecks().AddCheck($"{owner}-fact-cleanup", status.Read, tags: ["auditing-diagnostics"]);
        if (policy.Enabled)
        {
            services.AddSingleton<IHostedService>(provider => new CommittedFactCleanupWorker(
                provider.GetRequiredService<IServiceScopeFactory>(), owner, policy, status,
                provider.GetRequiredService<ILogger<CommittedFactCleanupWorker>>()));
        }
        return services;
    }
}

internal sealed partial class CommittedFactCleanupWorker(IServiceScopeFactory scopes, string owner,
    CommittedFactCleanupOptions options, CommittedFactCleanupStatus status, ILogger<CommittedFactCleanupWorker> logger) : BackgroundService
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
                var deleted = await scope.ServiceProvider.GetRequiredKeyedService<ICommittedFactCleanup>(owner).CleanupAsync(budget.Token).ConfigureAwait(false);
                status.Succeeded(deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                status.Failed();
                try { LogCleanupFailed(owner); }
                catch (Exception) { /* 诊断故障不停止维护，也不递归采集或输出原始异常。 */ }
            }
            try { await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "上下文 {Owner} 的已交付审计事实副本清理失败，下一轮重试。")]
    private partial void LogCleanupFailed(string owner);
}

internal sealed class CommittedFactCleanupStatus(bool enabled)
{
    private readonly Lock _gate = new();
    private long _failures;
    private long _runs;
    private long _deleted;
    private bool _degraded;
    internal void Succeeded(int deleted) { lock (_gate) { _degraded = false; _runs++; _deleted += deleted; } }
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
                ["deletedCopies"] = _deleted,
            };
            return _degraded ? HealthCheckResult.Degraded("已交付事实副本清理失败，下一轮重试。", data: data)
                : HealthCheckResult.Healthy(data: data);
        }
    }
}
