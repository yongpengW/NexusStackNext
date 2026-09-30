using NexusStackNext.Scheduling.Application;

namespace NexusStackNext.Scheduling.Endpoints;

/// <summary>
/// 调度后台服务。
/// <para>
/// <b>循环在这里，逻辑在 <see cref="ScheduleRunner"/>。</b>这个类只负责"每隔多久跑一轮"
/// 与"单轮失败怎么办"，因此它薄到几乎不需要测试；而一轮里该做什么是可确定性测试的。
/// </para>
/// <para>
/// 参照仓库的 <c>PlanTaskService</c> 是事实空壳：写调度缓存的种子服务注册被注释掉
/// （<c>Core/ServiceCollectionExtensions.cs:262</c>），于是 <c>CronScheduleService</c>
/// 永远静默跳过，还以 1Hz 空转——每轮新建一个 DI Scope、发一次 Redis GET，什么也不做。
/// 这里的循环有固定节拍，而"这一轮做了什么"会以日志与返回值的形式出现。
/// </para>
/// <para>
/// 日志用 <c>[LoggerMessage]</c> 源生成器而不是 <c>logger.LogInformation(...)</c>：
/// 前者在编译期生成格式化代码，禁用的日志级别不会付出装箱与字符串格式化的代价
/// （这是 CA1848/CA1873 要求的东西，也是它值得要求的原因）。
/// </para>
/// </summary>
/// <param name="runner">单轮执行器。</param>
/// <param name="logger">日志。</param>
public sealed partial class SchedulingWorker(ScheduleRunner runner, ILogger<SchedulingWorker> logger) : BackgroundService
{
    /// <summary>扫描节拍。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogInMemoryStoreWarning();

        using var timer = new PeriodicTimer(TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                var result = await runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result.Triggered > 0)
                {
                    LogTick(result.Examined, result.Triggered, result.Skipped);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // 单轮失败不终止循环：下一轮继续，否则一次瞬时故障会让调度永久停摆。
                LogTickFailed(exception);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Scheduling 当前使用内存存储：进程重启即丢失。持久化尚未接入。")]
    private partial void LogInMemoryStoreWarning();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "调度一轮：检查 {Examined} 个，触发 {Triggered} 个，跳过 {Skipped} 个。")]
    private partial void LogTick(int examined, int triggered, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "调度轮次失败，下一轮继续。")]
    private partial void LogTickFailed(Exception exception);
}
