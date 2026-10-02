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
/// 迟到的执行<b>不补跑</b>：下次时刻从"实际执行时刻"起算，而不是从原计划时刻起算。
/// 否则停机一小时后重启会瞬间涌入几十次补跑。
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
        Interval = source.Interval;
        IsEnabled = source.IsEnabled;
        LastRunAt = source.LastRunAt;
        NextRunAt = source.NextRunAt;
        TriggerSequence = source.TriggerSequence;
    }

    /// <summary>隔离读取与后续修改，保留标识和版本，不复制待发布事件。</summary>
    /// <returns>独立状态快照。</returns>
    public ScheduledTask Snapshot() => new(this);

    private ScheduledTask(ScheduledTaskId id, TaskCode code, TimeSpan interval, DateTimeOffset firstRunAt,
        ScheduleTarget target, string createdBy)
        : base(id)
    {
        Code = code;
        Interval = interval;
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

    /// <summary>执行间隔。</summary>
    public TimeSpan Interval { get; private set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>上次触发登记时刻；沿用 LastRunAt 存储与 HTTP 字段，不表示业务完成。</summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>下次计划时刻。</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>已经登记的发生序号；暂停与恢复不回退。</summary>
    public long TriggerSequence { get; private set; }

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
            : Result.Success(new ScheduledTask(id, code, interval, firstRunAt, target, createdBy));
    }

    /// <summary>此刻是否到期。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否该执行。</returns>
    public bool IsDue(DateTimeOffset now) => IsEnabled && NextRunAt is { } next && next <= now;

    /// <summary>登记一次触发并推进下次计划时刻。</summary>
    /// <param name="at">实际登记时刻。</param>
    /// <returns>成功，或任务未启用。</returns>
    public Result MarkTriggered(DateTimeOffset at)
    {
        if (!IsEnabled)
        {
            return Result.Failure(new Error("scheduling.task.disabled", "任务未启用，不能执行。"));
        }

        LastRunAt = at;
        NextRunAt = at + Interval;
        TriggerSequence++;
        Raise(new ScheduledTaskTriggered(Id, Code.Value, at, NextRunAt.Value));
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

        if (Interval == interval)
        {
            return Result.Success();
        }

        Interval = interval;
        return Changed();
    }

    /// <summary>启用任务并重置下次计划时刻。<b>这是一个改变</b>——下次时刻被重算了。</summary>
    /// <param name="from">起算时刻。</param>
    public void Enable(DateTimeOffset from)
    {
        var nextRunAt = from + Interval;

        // **空操作不是改变**（ADR-0011）。判据是"可观察状态有没有变"：
        // 已经启用、且下次时刻算出来还是同一个值时，版本号不动。
        // 缺了这句，一次"重新启用"会把一个什么都没变的聚合标成已修改。
        if (IsEnabled && NextRunAt == nextRunAt)
        {
            return;
        }

        IsEnabled = true;
        NextRunAt = nextRunAt;
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
        BumpVersion();
    }
}
