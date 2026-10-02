using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>
/// 计划存储：读取独立快照，新增编码唯一，修改按已观察版本原子提交。
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

    /// <summary>列出任务快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部任务，按编码排序。</returns>
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按稳定标识在存储内分页，不能先加载所有计划。</summary>
    /// <param name="page">1 到 1000。</param>
    /// <param name="limit">1 到 200。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>有界计划快照与总数。</returns>
    Task<ScheduledTaskPage> ReadPageAsync(int page, int limit, CancellationToken cancellationToken = default);

    /// <summary>按标识直接查找独立快照。</summary>
    /// <param name="id">计划标识。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>计划，或不存在。</returns>
    Task<ScheduledTask?> FindAsync(ScheduledTaskId id, CancellationToken cancellationToken = default);

    /// <summary>新增计划；编码竞争必须在存储内原子裁决。</summary>
    /// <param name="task">任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结果。</returns>
    Task<Result> AddAsync(ScheduledTask task, CancellationToken cancellationToken = default);

    /// <summary>原子比较原版本并保存；冲突不修改任何状态。</summary>
    /// <param name="task">修改后的独立快照。</param>
    /// <param name="expectedVersion">读取时的版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结果。</returns>
    Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>按版本原子保存已推进的计划、发生和 Outbox；重复同一次提交返回原结论。</summary>
    /// <param name="task">推进后的计划快照。</param>
    /// <param name="expectedVersion">推进前版本。</param>
    /// <param name="occurrence">重试期间保持不变的发生。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>登记成功或版本冲突。</returns>
    Task<Result> RecordOccurrenceAsync(ScheduledTask task, long expectedVersion, ScheduleOccurrence occurrence, CancellationToken cancellationToken = default);

    /// <summary>按发生序号倒序查询一个计划的触发历史，最多 100 条。</summary>
    /// <param name="planId">计划。</param>
    /// <param name="offset">非负偏移。</param>
    /// <param name="limit">1 到 100。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>有界历史及总量。</returns>
    Task<ScheduleOccurrencePage> ReadOccurrencesAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>仅在耗尽状态仍匹配时恢复原发生的交付；不生成新发生。</summary>
    /// <param name="occurrenceId">原发生标识。</param>
    /// <param name="expectedDeadLetteredAt">已观察的停止时刻。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>恢复后的交付状态，或条件冲突。</returns>
    Task<Result<ScheduleOccurrenceDelivery>> RetryOccurrenceAsync(Guid occurrenceId, DateTimeOffset expectedDeadLetteredAt, CancellationToken cancellationToken = default);
}

/// <summary>一轮调度扫描的结果。</summary>
/// <param name="Examined">读取到的到期任务数。</param>
/// <param name="Triggered">实际触发并推进的任务数。</param>
/// <param name="Skipped">因不可执行而跳过的任务数。</param>
public sealed record ScheduleRunResult(int Examined, int Triggered, int Skipped)
{
    /// <summary>登记失败的计划标识；不包含数据库异常或业务载荷。</summary>
    public IReadOnlyList<long> FailedPlanIds { get; init; } = Array.Empty<long>();
}

/// <summary>
/// 调度执行器：跑一轮。
/// <para>
/// <b>一轮的逻辑是纯的，循环是薄的。</b>后台服务只负责"每隔多久调用一次"，
/// 而"这一轮该做什么"在这里，于是它可以被确定性地测试——
/// 而不是像参照仓库那样，调度器的行为只能靠盯日志来猜。
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
        var instant = clock.UtcNow;
        var now = new DateTimeOffset(instant.UtcTicks - (instant.UtcTicks % 10), TimeSpan.Zero);
        var due = await store.ReadDueAsync(now, DefaultBatchSize, cancellationToken).ConfigureAwait(false);

        var triggered = 0;
        var skipped = 0;
        var failed = new List<long>();

        foreach (var task in due)
        {
            try
            {
                // 再判一次到期：存储可能返回了已被并发修改的任务。
                if (!task.IsDue(now))
                {
                    skipped++;
                    continue;
                }

                // **不丢弃结果**：`MarkTriggered` 在任务已停用时返回失败。
                // 今天这条路到不了（上面刚判过 `IsDue`），但"结果被丢掉"是个会腐烂的形状——
                // 将来谁改了 `IsDue` 或 `MarkTriggered` 的语义，这里会安静地少数一次触发。
                var expectedVersion = task.Version;
                var scheduledAt = task.NextRunAt!.Value;
                if (task.MarkTriggered(now).IsFailure)
                {
                    skipped++;
                    continue;
                }

                var occurrence = new ScheduleOccurrence(Guid.NewGuid(), task.Id.Value, task.TriggerSequence, scheduledAt, now,
                    task.Target.Kind, task.Target.SubjectId, task.CreatedBy);
                var saved = await store.RecordOccurrenceAsync(task, expectedVersion, occurrence, cancellationToken).ConfigureAwait(false);
                if (saved.IsSuccess) { triggered++; }
                else { skipped++; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed.Add(task.Id.Value);
            }
        }

        var result = new ScheduleRunResult(due.Count, triggered, skipped);
        return failed.Count == 0 ? result : result with { FailedPlanIds = failed.ToArray() };
    }
}
