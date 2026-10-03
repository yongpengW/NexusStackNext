using NexusStackNext.BuildingBlocks.Application.Operations;
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
    /// <param name="origin">首次定义的操作来源；随计划保存，之后不改写。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结果。</returns>
    Task<Result> AddAsync(ScheduledTask task, ExecutionOrigin? origin = null, CancellationToken cancellationToken = default);

    /// <summary>读取计划首次定义时保存的来源；旧计划可能没有该信息。</summary>
    /// <param name="id">计划标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最初来源，或未保存来源。</returns>
    Task<ExecutionOrigin?> ReadExecutionOriginAsync(ScheduledTaskId id, CancellationToken cancellationToken = default);

    /// <summary>原子比较原版本并保存；冲突不修改任何状态。</summary>
    /// <param name="task">修改后的独立快照。</param>
    /// <param name="expectedVersion">读取时的版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结果。</returns>
    Task<Result> SaveAsync(ScheduledTask task, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>按版本原子保存已推进的计划、调度决定及可选发生/Outbox；重复同一次提交返回原结论。</summary>
    /// <param name="task">推进后的计划快照。</param>
    /// <param name="expectedVersion">推进前版本。</param>
    /// <param name="decision">重试期间保持不变的决定。</param>
    /// <param name="occurrence">触发时的发生；跳过为空。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>登记成功或版本冲突。</returns>
    Task<Result> RecordDecisionAsync(ScheduledTask task, long expectedVersion, ScheduleDecision decision, ScheduleOccurrence? occurrence, CancellationToken cancellationToken = default);

    /// <summary>按提交版本倒序查询调度决定，最多 100 条。</summary>
    /// <param name="planId">所属计划。</param>
    /// <param name="offset">非负偏移。</param>
    /// <param name="limit">1 到 100。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>有界历史。</returns>
    Task<ScheduleDecisionPage> ReadDecisionsAsync(long planId, long offset, int limit, CancellationToken cancellationToken = default);

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
/// <param name="calendar">与预览共用的时刻计算。</param>
/// <param name="observations">后台执行观察；独立应用可不组合。</param>
/// <param name="execution">当前执行关联。</param>
public sealed class ScheduleRunner(IScheduledTaskStore store, IClock clock, IScheduleCalendar calendar,
    IBackgroundExecutionObservation? observations = null, IExecutionContext? execution = null)
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
            var original = task.Snapshot();
            try
            {
                // 再判一次到期：存储可能返回了已被并发修改的任务。
                if (!task.IsDue(now))
                {
                    skipped++;
                    continue;
                }

                var id = Guid.NewGuid();
                var attempt = observations is null
                    ? await DecideOrDeferAsync(task, original, id, now,
                        await store.ReadExecutionOriginAsync(task.Id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
                    : await observations.ObserveAsync(new ScheduleExecutionDescriptor("scheduling.decide", task.Id.Value, task.Version, id, task.DelegatedBy),
                        async () =>
                        {
                            var origin = await store.ReadExecutionOriginAsync(task.Id, cancellationToken).ConfigureAwait(false);
                            return new BackgroundExecutionInput<ExecutionOrigin?>(origin, origin);
                        },
                        origin => DecideOrDeferAsync(task, original, id, now, origin, cancellationToken),
                        static result => result switch
                        {
                            DecisionResult.Triggered => BackgroundExecutionOutcome.Accepted,
                            DecisionResult.Skipped => BackgroundExecutionOutcome.Skipped,
                            DecisionResult.Rejected => BackgroundExecutionOutcome.Rejected,
                            _ => BackgroundExecutionOutcome.Failed,
                        }, cancellationToken).ConfigureAwait(false);
                if (attempt == DecisionResult.Triggered) { triggered++; }
                else if (attempt == DecisionResult.Failed) { failed.Add(task.Id.Value); }
                else { skipped++; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed.Add(task.Id.Value);
                await DeferAsync(original, now, "scheduling.commit.failed", cancellationToken).ConfigureAwait(false);
            }
        }

        var result = new ScheduleRunResult(due.Count, triggered, skipped);
        return failed.Count == 0 ? result : result with { FailedPlanIds = failed.ToArray() };
    }

    private async Task<DecisionResult> DecideOrDeferAsync(ScheduledTask task, ScheduledTask original, Guid id, DateTimeOffset now,
        ExecutionOrigin? origin, CancellationToken cancellationToken)
    {
        try { return await DecideAsync(task, original, id, now, origin, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 退避是本次失败执行提交的状态：在观察作用域结束前保存，保留当前操作与原发起关联。
            await DeferAsync(original, now, "scheduling.commit.failed", cancellationToken).ConfigureAwait(false);
            return DecisionResult.Failed;
        }
    }

    private async Task<DecisionResult> DecideAsync(ScheduledTask task, ScheduledTask original, Guid id, DateTimeOffset now,
        ExecutionOrigin? origin, CancellationToken cancellationToken)
    {
        var expectedVersion = task.Version;
        var scheduledAt = task.NextRunAt!.Value;
        var next = calendar.NextOccurrence(task.Rule, now);
        if (next.IsFailure)
        {
            await DeferAsync(original, now, next.Error.Code, cancellationToken).ConfigureAwait(false);
            return DecisionResult.Failed;
        }
        var misfire = task.Rule.Kind != "Interval" && now - scheduledAt > TimeSpan.FromSeconds(task.Rule.GraceSeconds!.Value);
        var trigger = !misfire || task.Rule.MisfirePolicy != "Skip";
        if (task.Advance(now, next.Value, trigger).IsFailure) { return DecisionResult.Rejected; }

        var occurrence = trigger ? new ScheduleOccurrence(id, task.Id.Value, task.TriggerSequence, scheduledAt, now,
            task.Target.Kind, task.Target.SubjectId, task.DelegatedBy)
        { ExecutionOrigin = execution?.Capture() ?? origin } : null;
        var decision = new ScheduleDecision(id, task.Id.Value, task.Version, task.ScheduleRevision, task.Rule,
            !trigger ? "Skipped" : misfire ? "Coalesced" : "Triggered", scheduledAt, now, next.Value, occurrence?.OccurrenceId);
        var saved = await store.RecordDecisionAsync(task, expectedVersion, decision, occurrence, cancellationToken).ConfigureAwait(false);
        return saved.IsFailure ? DecisionResult.Rejected : trigger ? DecisionResult.Triggered : DecisionResult.Skipped;
    }

    private enum DecisionResult { Triggered, Skipped, Rejected, Failed }

    private async Task DeferAsync(ScheduledTask original, DateTimeOffset now, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            var version = original.Version;
            if (original.Defer(now, errorCode).IsSuccess)
            {
                // 使用推进前的快照及版本：失败或未知提交结果不得覆盖已成功的决定，也不得恢复已暂停的计划。
                await store.SaveAsync(original, version, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 存储本身不可用时无法持久化退避；该计划仍报告失败，继续处理本批其他计划。
        }
    }
}
