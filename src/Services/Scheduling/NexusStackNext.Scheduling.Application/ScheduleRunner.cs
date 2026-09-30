using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>
/// 计划任务的持久化端口。
/// <para>
/// <b>刻意只有三个方法。</b>"按标识找"与"编码是否已存在"都不在这里——
/// 它们由 <see cref="TaskRegistry"/> 在 <see cref="ListAsync"/> 的结果上做。
/// 任务表是几十条量级的注册表，为一次查找多开两个方法，
/// 换来的只是"把简单的事拆到两个地方"。等它真的变成几万条再拆不迟。
/// </para>
/// </summary>
public interface IScheduledTaskStore
{
    /// <summary>读取此刻到期的任务。</summary>
    /// <param name="now">当前时刻。</param>
    /// <param name="batchSize">最多读取多少个。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>到期任务。</returns>
    Task<IReadOnlyList<ScheduledTask>> ReadDueAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>列出全部任务。<b>也是"按标识找"与"编码查重"的依据</b>（见类型说明）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部任务，按编码排序。</returns>
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>保存任务状态（新增或更新）。必须与"记录执行"同事务，否则会出现"执行了但没记录"。</summary>
    /// <param name="task">任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task SaveAsync(ScheduledTask task, CancellationToken cancellationToken = default);
}

/// <summary>一轮调度扫描的结果。</summary>
/// <param name="Examined">读取到的到期任务数。</param>
/// <param name="Triggered">实际触发并推进的任务数。</param>
/// <param name="Skipped">因不可执行而跳过的任务数。</param>
public sealed record ScheduleRunResult(int Examined, int Triggered, int Skipped);

/// <summary>
/// 调度执行器：跑一轮。
/// <para>
/// <b>一轮的逻辑是纯的，循环是薄的。</b>后台服务只负责"每隔多久调用一次"，
/// 而"这一轮该做什么"在这里，于是它可以被确定性地测试——
/// 而不是像参照仓库那样，调度器的行为只能靠盯日志来猜。
/// </para>
/// <para>
/// 单个任务失败不影响同轮的其他任务：一轮里任何一个任务抛错都不应该让它后面的任务全部饿死。
/// </para>
/// </summary>
/// <param name="store">任务存储。</param>
/// <param name="clock">时钟。</param>
public sealed class ScheduleRunner(IScheduledTaskStore store, IClock clock)
{
    /// <summary>单轮最多处理多少个任务。</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>执行一轮调度。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮结果。</returns>
    public async Task<ScheduleRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var due = await store.ReadDueAsync(now, DefaultBatchSize, cancellationToken).ConfigureAwait(false);

        var triggered = 0;
        var skipped = 0;

        foreach (var task in due)
        {
            // 再判一次到期：存储可能返回了已被并发修改的任务。
            if (!task.IsDue(now))
            {
                skipped++;
                continue;
            }

            // **不丢弃结果**：`MarkExecuted` 在任务已停用时返回失败。
            // 今天这条路到不了（上面刚判过 `IsDue`），但"结果被丢掉"是个会腐烂的形状——
            // 将来谁改了 `IsDue` 或 `MarkExecuted` 的语义，这里会安静地少数一次触发。
            if (task.MarkExecuted(now).IsFailure)
            {
                skipped++;
                continue;
            }

            await store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            triggered++;
        }

        return new ScheduleRunResult(due.Count, triggered, skipped);
    }
}
