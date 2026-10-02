using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Scheduling.Domain.Tasks;

/// <summary>计划任务标识。</summary>
public sealed record ScheduledTaskId : StronglyTypedId<long>
{
    /// <summary>由底层值构造。</summary>
    /// <param name="value">底层值。</param>
    public ScheduledTaskId(long value)
        : base(value)
    {
    }
}

/// <summary>任务编码：稳定、机器可读，用于标识"这是哪个任务"。</summary>
public sealed class TaskCode : ValueObject
{
    /// <summary>最大长度。</summary>
    public const int MaxLength = 64;

    private TaskCode(string value) => Value = value;

    /// <summary>规范化后的编码（小写）。</summary>
    public string Value { get; }

    /// <summary>构造任务编码。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>成功时返回值对象。</returns>
    public static Result<TaskCode> Create(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result.Failure<TaskCode>(new Error("scheduling.task_code.empty", "任务编码不能为空。"));
        }

        if (trimmed.Length > MaxLength)
        {
            return Result.Failure<TaskCode>(new Error(
                "scheduling.task_code.too_long",
                $"任务编码不能超过 {MaxLength} 个字符。"));
        }

        return trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? Result.Success(new TaskCode(trimmed))
            : Result.Failure<TaskCode>(new Error(
                "scheduling.task_code.format",
                "任务编码只允许小写字母、数字、点、短横线与下划线。"));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>计划登记了一次触发；业务接受和执行由目标上下文负责。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Code">任务编码。</param>
/// <param name="OccurredAt">触发登记时刻（UTC）。</param>
/// <param name="NextRunAt">下次计划时刻。</param>
public sealed record ScheduledTaskTriggered(
    ScheduledTaskId TaskId,
    string Code,
    DateTimeOffset OccurredAt,
    DateTimeOffset NextRunAt) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.NewGuid();
}

/// <summary>
/// 计划任务聚合根。
/// <para>
/// <b>核心不变量：每次执行后，下次计划时刻必须严格向前推进。</b>
/// 参照仓库的 <c>CronScheduleService</c> 正是因为调度键从未写入而永远静默跳过
/// （<c>Core/ServiceCollectionExtensions.cs:262</c> 把种子服务注册注释掉了），
/// 同时以 1Hz 空转：每轮新建一个 DI Scope、发一次 Redis GET，什么也不做。
/// 一个"不推进下次时刻"的调度器就是那样变成空转的。
/// </para>
/// <para>
/// 一次决定最多产生一次业务意图，下一时刻严格晚于本轮观察时刻。
/// 固定间隔从实际登记时刻起算；日历规则由应用层计算下一发生，避免无界补跑。
/// </para>
/// </summary>
public sealed class ScheduledTask : AggregateRoot<ScheduledTaskId>
{
    /// <summary>固定间隔的最小值。</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    /// <summary>固定间隔的最大值；日历计划另用明确时区的规则表达。</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(366);

    private ScheduledTask(ScheduledTaskId id) : base(id) { }

    private ScheduledTask(ScheduledTask source) : base(source)
    {
        Code = source.Code;
        Target = source.Target;
        CreatedBy = source.CreatedBy;
        Rule = source.Rule;
        ScheduleRevision = source.ScheduleRevision;
        IsEnabled = source.IsEnabled;
        LastRunAt = source.LastRunAt;
        NextRunAt = source.NextRunAt;
        TriggerSequence = source.TriggerSequence;
        RetryAt = source.RetryAt;
        LastSchedulingErrorCode = source.LastSchedulingErrorCode;
        SchedulingFailureCount = source.SchedulingFailureCount;
    }

    /// <summary>隔离读取与后续修改，保留标识和版本，不复制待发布事件。</summary>
    /// <returns>独立状态快照。</returns>
    public ScheduledTask Snapshot() => new(this);

    private ScheduledTask(ScheduledTaskId id, TaskCode code, ScheduleRule rule, DateTimeOffset firstRunAt,
        ScheduleTarget target, string createdBy)
        : base(id)
    {
        Code = code;
        Rule = rule;
        NextRunAt = firstRunAt;
        IsEnabled = true;
        Target = target;
        CreatedBy = createdBy;
    }

    /// <summary>任务编码。</summary>
    public TaskCode Code { get; private set; } = null!;

    /// <summary>创建时确定的业务执行目标。</summary>
    public ScheduleTarget Target { get; private set; } = null!;

    /// <summary>授权创建该后台委托的操作者标识。</summary>
    public string CreatedBy { get; private set; } = string.Empty;

    /// <summary>不可变的当前计划规则。</summary>
    public ScheduleRule Rule { get; private set; } = null!;

    /// <summary>规则修订；启停或触发不会改变它。</summary>
    public long ScheduleRevision { get; private set; } = 1;

    /// <summary>固定间隔；日历计划没有间隔。</summary>
    public TimeSpan? Interval => Rule.IntervalSeconds is { } seconds ? ScheduleRule.NormalizeInterval(seconds) : null;

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>上次触发登记时刻；沿用 LastRunAt 存储与 HTTP 字段，不表示业务完成。</summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>下次计划时刻。</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>已经登记的发生序号；暂停与恢复不回退。</summary>
    public long TriggerSequence { get; private set; }

    /// <summary>失败后最早再次扫描的时刻，不改变原计划时刻。</summary>
    public DateTimeOffset? RetryAt { get; private set; }

    /// <summary>最近一次调度失败的稳定错误码；不保存异常文本。</summary>
    public string? LastSchedulingErrorCode { get; private set; }

    /// <summary>连续调度失败次数，成功或有效管理修改后归零。</summary>
    public int SchedulingFailureCount { get; private set; }

    /// <summary>创建计划任务。</summary>
    /// <param name="id">标识。</param>
    /// <param name="code">任务编码。</param>
    /// <param name="interval">执行间隔，必须为正。</param>
    /// <param name="firstRunAt">首次执行时刻。</param>
    /// <param name="target">固定业务目标。</param>
    /// <param name="createdBy">已验证的操作者标识。</param>
    /// <returns>成功时返回任务。</returns>
    public static Result<ScheduledTask> Create(
        ScheduledTaskId id,
        TaskCode code,
        TimeSpan interval,
        DateTimeOffset firstRunAt,
        ScheduleTarget target,
        string createdBy)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Length > 128 || createdBy.Any(char.IsControl)) { return Result.Failure<ScheduledTask>(new Error("scheduling.actor.invalid", "计划必须由已验证的操作者创建。")); }

        return interval < MinimumInterval || interval > MaximumInterval
            ? Result.Failure<ScheduledTask>(new Error("scheduling.interval.invalid", "执行间隔必须在 1 秒到 366 天之间。"))
            : Create(id, code, ScheduleRule.FixedInterval(interval), firstRunAt, target, createdBy);
    }

    /// <summary>用已验证的规则和首次时刻创建计划；日历可执行性由应用层计算。</summary>
    /// <param name="id">标识。</param>
    /// <param name="code">稳定编码。</param>
    /// <param name="rule">不可变规则。</param>
    /// <param name="firstRunAt">已经计算的首次时刻。</param>
    /// <param name="target">固定业务目标。</param>
    /// <param name="createdBy">已验证的委托人。</param>
    /// <returns>计划或错误。</returns>
    public static Result<ScheduledTask> Create(ScheduledTaskId id, TaskCode code, ScheduleRule rule,
        DateTimeOffset firstRunAt, ScheduleTarget target, string createdBy)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(target);
        if (!rule.IsValid) { return Result.Failure<ScheduledTask>(ScheduleRule.Invalid); }
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Length > 128 || createdBy.Any(char.IsControl))
        {
            return Result.Failure<ScheduledTask>(new Error("scheduling.actor.invalid", "计划必须由已验证的操作者创建。"));
        }
        return Result.Success(new ScheduledTask(id, code, rule, firstRunAt, target, createdBy));
    }

    /// <summary>此刻是否到期。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否该执行。</returns>
    public bool IsDue(DateTimeOffset now) => IsEnabled && NextRunAt is { } next && next <= now && (RetryAt is null || RetryAt <= now);

    /// <summary>失败退避：1、2、4、8、16、32 分钟，之后每小时重试；业务状态保持原值。</summary>
    /// <param name="at">失败的本轮观察时刻。</param>
    /// <param name="errorCode">稳定且有界的错误码。</param>
    /// <returns>退避状态，或计划未到期。</returns>
    public Result Defer(DateTimeOffset at, string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode.Length, 96);
        if (!IsDue(at)) { return Result.Failure(new Error("scheduling.task.not_due", "计划未到期或已经停用。")); }
        if (SchedulingFailureCount < int.MaxValue) { SchedulingFailureCount++; }
        RetryAt = at.AddMinutes(Math.Min(60, 1 << Math.Min(SchedulingFailureCount - 1, 6)));
        LastSchedulingErrorCode = errorCode;
        return Changed();
    }

    /// <summary>登记一次触发并推进下次计划时刻。</summary>
    /// <param name="at">实际登记时刻。</param>
    /// <returns>成功，或任务未启用。</returns>
    public Result MarkTriggered(DateTimeOffset at)
    {
        if (Interval is not { } interval) { return Result.Failure(ScheduleRule.Invalid); }
        return Advance(at, at + interval, trigger: true);
    }

    /// <summary>应用一次调度决定；无论触发或跳过，下一时刻都必须晚于处理时刻。</summary>
    /// <param name="at">唯一处理时刻。</param>
    /// <param name="nextRunAt">已计算的下一时刻。</param>
    /// <param name="trigger">是否产生一次业务意图；跳过不增加发生序号。</param>
    /// <returns>状态推进，或不满足到期/严格向前的不变量。</returns>
    public Result Advance(DateTimeOffset at, DateTimeOffset nextRunAt, bool trigger)
    {
        if (!IsDue(at)) { return Result.Failure(new Error("scheduling.task.not_due", "计划未到期或已经停用。")); }
        if (nextRunAt <= at) { return Result.Failure(new Error("scheduling.next_run.invalid", "下次计划时刻必须晚于处理时刻。")); }
        NextRunAt = nextRunAt;
        ClearFailure();
        if (trigger)
        {
            LastRunAt = at;
            TriggerSequence++;
            Raise(new ScheduledTaskTriggered(Id, Code.Value, at, nextRunAt));
        }
        return Changed();
    }

    /// <summary>修改执行间隔。传入相同的间隔不是改变。</summary>
    /// <param name="interval">新间隔。</param>
    /// <returns>成功，或间隔不为正。</returns>
    public Result ChangeInterval(TimeSpan interval)
    {
        if (interval < MinimumInterval || interval > MaximumInterval)
        {
            return Result.Failure(new Error("scheduling.interval.invalid", "执行间隔必须在 1 秒到 366 天之间。"));
        }

        var rule = ScheduleRule.FixedInterval(interval);
        if (Rule == rule)
        {
            return Result.Success();
        }

        Rule = rule;
        ScheduleRevision++;
        ClearFailure();
        return Changed();
    }

    /// <summary>变更规则并只重排未来；相同规范化规则不改变任何可观察状态。</summary>
    /// <param name="rule">已验证的新规则。</param>
    /// <param name="at">本次管理操作时刻。</param>
    /// <param name="nextRunAt">严格晚于操作时刻的新发生。</param>
    /// <returns>更新或不变量错误。</returns>
    public Result ChangeRule(ScheduleRule rule, DateTimeOffset at, DateTimeOffset nextRunAt)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.IsValid) { return Result.Failure(ScheduleRule.Invalid); }
        if (rule == Rule) { return Result.Success(); }
        if (nextRunAt <= at) { return Result.Failure(new Error("scheduling.next_run.invalid", "新规则的下次时刻必须晚于修改时刻。")); }
        Rule = rule;
        ScheduleRevision++;
        NextRunAt = IsEnabled ? nextRunAt : null;
        ClearFailure();
        return Changed();
    }

    /// <summary>启用任务并重置下次计划时刻。<b>这是一个改变</b>——下次时刻被重算了。</summary>
    /// <param name="from">起算时刻。</param>
    public void Enable(DateTimeOffset from)
    {
        if (Interval is not { } interval) { throw new InvalidOperationException("日历计划必须提供计算后的下次时刻。"); }
        EnableAt(from + interval);
    }

    /// <summary>启用并使用应用层计算的下次时刻；相同状态不增加版本。</summary>
    /// <param name="nextRunAt">计算后的未来时刻。</param>
    public void EnableAt(DateTimeOffset nextRunAt)
    {

        // **空操作不是改变**（ADR-0011）。判据是"可观察状态有没有变"：
        // 已经启用、且下次时刻算出来还是同一个值时，版本号不动。
        // 缺了这句，一次"重新启用"会把一个什么都没变的聚合标成已修改。
        if (IsEnabled && NextRunAt == nextRunAt && SchedulingFailureCount == 0)
        {
            return;
        }

        IsEnabled = true;
        NextRunAt = nextRunAt;
        ClearFailure();
        BumpVersion();
    }

    /// <summary>停用任务。<b>同时清空下次计划时刻</b>，否则它仍会被判为到期。重复停用不是改变。</summary>
    public void Disable()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsEnabled = false;
        NextRunAt = null;
        RetryAt = null;
        BumpVersion();
    }

    private void ClearFailure()
    {
        RetryAt = null;
        LastSchedulingErrorCode = null;
        SchedulingFailureCount = 0;
    }
}
