using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NexusStackNext.Auditing.Application;

namespace NexusStackNext.Auditing.Infrastructure.Persistence;

internal sealed class OperationJournalStartupCheck(IDbContextFactory<OperationJournalDbContext> contexts) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var context = await contexts.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            await OperationJournalDatabase.CheckSchemaAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("OperationJournal 数据库不可用或需要迁移；检查 ConnectionStrings:OperationJournal 并执行 migrate-operation-journal。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class OperationJournalHealthCheck(IDbContextFactory<OperationJournalDbContext> contexts, OperationJournalStatus status,
    OperationJournalCapacityOptions capacity) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var context = await contexts.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            var usage = await OperationJournalDatabase.CheckSchemaAsync(context, timeout.Token).ConfigureAwait(false);
            var stopped = await context.Outbox.AnyAsync(entry => entry.DeadLetteredAt != null, timeout.Token).ConfigureAwait(false);
            var recoveries = await context.Set<OperationJournalRecoveryRecord>().LongCountAsync(timeout.Token).ConfigureAwait(false);
            return OperationJournalHealth.Result(status, stopped, usage.RecordCount, usage.PayloadBytes, recoveries, capacity);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // 普通操作日志运行期失效不能让网关摘除仍可处理业务的宿主。
            // 启动仍严格检查配置和迁移；运行期保留降级状态及累计缺失供调查。
            return HealthCheckResult.Degraded("OperationJournal 数据库不可用或需要迁移。",
                data: new Dictionary<string, object> { ["failedWrites"] = status.FailureCount, ["storageAvailable"] = false });
        }
    }
}

internal sealed class InMemoryOperationJournalHealthCheck(InMemoryOperationJournal journal, OperationJournalStatus status,
    OperationJournalCapacityOptions capacity) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        var usage = journal.Usage();
        return Task.FromResult(OperationJournalHealth.Result(status, journal.HasDeadLetters,
            usage.Records, usage.PayloadBytes, usage.RecoveryRecords, capacity));
    }
}

internal static class OperationJournalHealth
{
    internal static HealthCheckResult Result(OperationJournalStatus status, bool stopped, long records, long bytes, long recoveries,
        OperationJournalCapacityOptions capacity)
    {
        var full = records >= capacity.MaxRecords || bytes >= capacity.MaxPayloadBytes;
        var recoveryFull = recoveries >= capacity.MaxRecoveryRecords;
        var data = new Dictionary<string, object>
        {
            ["failedWrites"] = status.FailureCount,
            ["hasDeadLetters"] = stopped,
            ["storageAvailable"] = true,
            ["retainedRecords"] = records,
            ["retainedPayloadBytes"] = bytes,
            ["maxRecords"] = capacity.MaxRecords,
            ["maxPayloadBytes"] = capacity.MaxPayloadBytes,
            ["capacityReached"] = full,
            ["retainedRecoveryRecords"] = recoveries,
            ["maxRecoveryRecords"] = capacity.MaxRecoveryRecords,
            ["recoveryCapacityReached"] = recoveryFull,
            ["cleanupFailures"] = status.CleanupFailureCount,
            ["cleanupDegraded"] = status.CleanupDegraded,
        };
        return status.FailureCount > 0 || stopped || full || recoveryFull || status.CleanupDegraded
            ? HealthCheckResult.Degraded("OperationJournal 存在采集缺口、停止投递或容量不足；请检查日志诊断。", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}
