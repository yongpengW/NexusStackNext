using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Costing.Contracts;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>
/// 计划任务的注册表：定义、查询、启停与规则更新。
///
/// <para>校验业务目标、生成标识并选择首次时刻；编码唯一性与版本竞争由存储原子裁决。</para>
///
/// <para><b>停用会清空下次计划时刻</b>（聚合保证）。这一条很容易写错：
/// 只把 <c>IsEnabled</c> 置 false 而留着 <c>NextRunAt</c>，
/// 任务在 <c>ReadDueAsync</c> 眼里仍然是"到期"的——于是它每轮都被读出来、每轮都被跳过，
/// 日志上看起来一切正常，实际上调度器在空转。</para>
///
/// <para>调用方通过预期版本管理计划；时刻计算、唯一性和持久化由本模块完成。</para>
/// </summary>
/// <param name="store">任务存储。</param>
/// <param name="ids">标识生成器。</param>
/// <param name="clock">时钟。</param>
/// <param name="calendar">日历计算。</param>
public sealed class TaskRegistry(IScheduledTaskStore store, IIdGenerator ids, IClock clock, IScheduleCalendar calendar)
{
    /// <summary>管理查询的最大页码。</summary>
    public const int MaximumPage = 1000;
    /// <summary>每页最多列出的计划数。</summary>
    public const int MaximumPageSize = 200;
    /// <summary>调用方已观察的计划版本不再有效。</summary>
    public static readonly Error Conflict = new("scheduling.version_conflict", "计划已被修改，请读取最新版本。");
    /// <summary>任务编码已有定义。</summary>
    public static readonly Error CodeTaken = new("scheduling.task_code.taken", "任务编码已存在。");

    /// <summary>定义显式规则计划；日历首次时刻与预览相同，Interval 立即到期。</summary>
    /// <param name="code">唯一编码。</param>
    /// <param name="rule">调用方规则。</param>
    /// <param name="target">创建后固定的目标。</param>
    /// <param name="createdBy">已验证的委托人。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>计划或验证错误。</returns>
    public async Task<Result<ScheduledTask>> DefineAsync(TaskCode code, ScheduleRuleInput rule, ScheduleTarget target,
        string createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != CostingScheduleTarget.Recalculate) { return Result.Failure<ScheduledTask>(ScheduleTarget.Invalid); }
        var now = clock.UtcNow;
        var preview = calendar.Preview(rule, now, 1);
        if (preview.IsFailure) { return Result.Failure<ScheduledTask>(preview.Error); }
        var created = ScheduledTask.Create(new ScheduledTaskId(ids.NextId()), code, preview.Value.Rule,
            preview.Value.Rule.Kind == "Interval" ? now : preview.Value.Times[0].Utc, target, createdBy);
        if (created.IsFailure) { return created; }
        var saved = await store.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? created : Result.Failure<ScheduledTask>(saved.Error);
    }

    /// <summary>定义一个计划任务。</summary>
    /// <param name="code">任务编码，必须唯一。</param>
    /// <param name="interval">执行间隔，必须为正。</param>
    /// <param name="target">创建后固定的目标。</param>
    /// <param name="createdBy">已验证的委托人。</param>
    /// <param name="firstRunAt">首次执行时刻；不传则以"现在"起算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时返回任务；编码已存在或间隔非法则失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> 为 <c>null</c>。</exception>
    public async Task<Result<ScheduledTask>> DefineAsync(
        TaskCode code,
        TimeSpan interval,
        ScheduleTarget target,
        string createdBy,
        DateTimeOffset? firstRunAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != CostingScheduleTarget.Recalculate) { return Result.Failure<ScheduledTask>(ScheduleTarget.Invalid); }

        var created = ScheduledTask.Create(
            new ScheduledTaskId(ids.NextId()),
            code,
            interval,
            firstRunAt ?? clock.UtcNow, target, createdBy);

        if (created.IsFailure)
        {
            return created;
        }

        var saved = await store.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? created : Result.Failure<ScheduledTask>(saved.Error);
    }

    /// <summary>列出全部任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按编码排序的任务。</returns>
    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    /// <summary>按标识找一个任务。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回任务，否则 <c>null</c>。</returns>
    public Task<ScheduledTask?> FindAsync(
        ScheduledTaskId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return store.FindAsync(id, cancellationToken);
    }

    /// <summary>停用一个任务。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="expectedVersion">调用方观察到的版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或任务不存在。</returns>
    public Task<Result> PauseAsync(ScheduledTaskId id, long expectedVersion, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, expectedVersion, enable: false, cancellationToken);

    /// <summary>启用一个任务，并从"现在"重新起算下次执行。</summary>
    /// <param name="id">任务标识。</param>
    /// <param name="expectedVersion">调用方观察到的版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功，或任务不存在。</returns>
    public Task<Result> ResumeAsync(ScheduledTaskId id, long expectedVersion, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, expectedVersion, enable: true, cancellationToken);

    /// <summary>按已观察版本更新规则，不修改已经提交的发生或调度决定。</summary>
    /// <param name="id">计划。</param>
    /// <param name="expectedVersion">调用方观察到的版本。</param>
    /// <param name="rule">新规则。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>更新或条件冲突。</returns>
    public async Task<Result> UpdateRuleAsync(ScheduledTaskId id, long expectedVersion, ScheduleRuleInput rule, CancellationToken cancellationToken = default)
    {
        var task = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null) { return Result.Failure(new Error("scheduling.task.not_found", "计划不存在。")); }
        if (task.Version != expectedVersion) { return Result.Failure(Conflict); }
        var normalized = calendar.Normalize(rule);
        if (normalized.IsFailure) { return Result.Failure(normalized.Error); }
        if (normalized.Value == task.Rule)
        {
            // 已接受的稀疏规则不因当前预览窗口为空而失去空操作语义；存储仍须裁决并发版本。
            return await store.SaveAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        var now = clock.UtcNow;
        var preview = calendar.Preview(rule, now, 1);
        if (preview.IsFailure) { return Result.Failure(preview.Error); }
        var changed = task.ChangeRule(preview.Value.Rule, now, preview.Value.Times[0].Utc);
        if (changed.IsFailure) { return changed; }
        return await store.SaveAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> ChangeEnabledAsync(
        ScheduledTaskId id,
        long expectedVersion,
        bool enable,
        CancellationToken cancellationToken)
    {
        var task = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return Result.Failure(new Error("scheduling.task.not_found", $"任务不存在：{id.Value}。"));
        }
        if (task.Version != expectedVersion) { return Result.Failure(Conflict); }

        if (enable)
        {
            // 从"现在"起算，而不是补跑停用期间欠下的那些次。
            if (task.Interval is not null) { task.Enable(clock.UtcNow); }
            else
            {
                var next = calendar.NextOccurrence(task.Rule, clock.UtcNow);
                if (next.IsFailure) { return Result.Failure(next.Error); }
                task.EnableAt(next.Value);
            }
        }
        else
        {
            task.Disable();
        }

        return await store.SaveAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>在所属存储内分页的计划列表。</summary>
/// <param name="Items">当前页，按稳定计划标识排序。</param>
/// <param name="Total">总计划数。</param>
public sealed record ScheduledTaskPage(IReadOnlyList<ScheduledTask> Items, long Total);
