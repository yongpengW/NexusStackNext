using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Scheduling.Domain.Tasks;

/// <summary>经过应用层日历计算验证的不可变计划规则；不在领域内解析时区。</summary>
/// <param name="Kind">规则种类。</param>
/// <param name="Expression">规范化表达式。</param>
/// <param name="TimeZoneId">显式 IANA 时区。</param>
/// <param name="CronFieldCount">持久化的 Cron 格式：五或六字段。</param>
/// <param name="Day">MonthlyDay 的当地日。</param>
/// <param name="Hour">MonthlyDay 的当地小时。</param>
/// <param name="Minute">MonthlyDay 的当地分钟。</param>
/// <param name="MisfirePolicy">超过宽限的处理方式。</param>
/// <param name="GraceSeconds">允许的迟到时长，单位秒。</param>
/// <param name="IntervalSeconds">仅 Interval 规则具有固定间隔。</param>
public sealed record ScheduleRule(string Kind, string? Expression, string? TimeZoneId, int? CronFieldCount,
    int? Day = null, int? Hour = null, int? Minute = null, string? MisfirePolicy = null, double? GraceSeconds = null,
    double? IntervalSeconds = null)
{
    /// <summary>不合法或混合的规则形状。</summary>
    public static readonly Error Invalid = new("scheduling.rule.invalid", "计划规则的种类和字段不一致。");

    /// <summary>构造固定间隔规则，范围由聚合校验。</summary>
    /// <param name="interval">固定间隔。</param>
    /// <returns>规则值。</returns>
    public static ScheduleRule FixedInterval(TimeSpan interval) => new("Interval", null, null, null, IntervalSeconds: NormalizeInterval(interval.TotalSeconds).TotalSeconds);

    /// <summary>固定间隔统一到微秒精度，使用最近偶数舍入，避免秒数往返时丢失一个 tick。</summary>
    /// <param name="seconds">秒数；调用方负责合法间隔范围验证。</param>
    /// <returns>规范化间隔。</returns>
    public static TimeSpan NormalizeInterval(double seconds) =>
        // 先恢复十进制秒的语义，避免 1.0000005 的二进制表示把中点误推到上侧。
        TimeSpan.FromTicks(checked((long)Math.Round((decimal)seconds * 1_000_000, MidpointRounding.ToEven) * 10));

    // 领域校验状态形状；表达式与时区的可执行性由应用层日历接口校验。
    internal bool IsValid => Kind == "Interval"
        ? IntervalSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 1 && seconds <= 31622400
            && Expression is null && TimeZoneId is null && CronFieldCount is null && Day is null && Hour is null && Minute is null
            && MisfirePolicy is null && GraceSeconds is null
        : IntervalSeconds is null && TimeZoneId is { Length: > 0 and <= 128 }
            && MisfirePolicy is "FireOnce" or "Skip" && GraceSeconds is >= 1 and <= 3600
            && (Kind == "Cron" && Expression is { Length: > 0 and <= 256 } && CronFieldCount is 5 or 6 && Day is null && Hour is null && Minute is null
                || Kind == "MonthlyDay" && Expression is null && CronFieldCount is null && Day is >= 1 and <= 31 && Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59);
}
