using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>日历解析与时区计算的接口；创建、预览和运行期共用同一套计算规则。</summary>
public interface IScheduleCalendar
{
    /// <summary>验证并规范化规则形状与时区，不要求预览窗口内有发生；用于识别已接受规则的空操作。</summary>
    /// <param name="input">调用方规则。</param>
    /// <returns>规范化规则，或校验错误。</returns>
    Result<ScheduleRule> Normalize(ScheduleRuleInput input);

    /// <summary>验证并规范化规则，返回起点之后的有界预览，不写入计划。</summary>
    /// <param name="input">调用方规则。</param>
    /// <param name="after">排他的起点。</param>
    /// <param name="count">结果数量，1 到 10。</param>
    /// <returns>规范化规则与发生时刻，或明确校验错误。</returns>
    Result<SchedulePreview> Preview(ScheduleRuleInput input, DateTimeOffset after, int count);

    /// <summary>按持久规则计算严格晚于起点的下一时刻；有界搜索一个 Gregorian 400 年周期。</summary>
    /// <param name="rule">已保存的规则。</param>
    /// <param name="after">排他的 UTC 起点。</param>
    /// <returns>下一时刻，或规则当前不可计算。</returns>
    Result<DateTimeOffset> NextOccurrence(ScheduleRule rule, DateTimeOffset after);
}

/// <summary>调用方提交的计划规则；规范化结果由预览返回。</summary>
/// <param name="Kind">规则种类。</param>
/// <param name="Expression">五或六字段 Cron。</param>
/// <param name="TimeZoneId">显式 IANA 时区。</param>
/// <param name="Day">MonthlyDay 的日 1 到 31。</param>
/// <param name="Hour">MonthlyDay 的小时 0 到 23。</param>
/// <param name="Minute">MonthlyDay 的分钟 0 到 59。</param>
/// <param name="IntervalSeconds">固定间隔，与日历字段互斥。</param>
/// <param name="CronFieldCount">可选的显式字段数，传入时必须匹配表达式。</param>
/// <param name="MisfirePolicy">FireOnce 或 Skip；日历默认 FireOnce。</param>
/// <param name="GraceSeconds">日历宽限 1 秒到 1 小时，默认 30 秒。</param>
public sealed record ScheduleRuleInput(string? Kind, string? Expression = null, string? TimeZoneId = null,
    int? Day = null, int? Hour = null, int? Minute = null, double? IntervalSeconds = null,
    int? CronFieldCount = null, string? MisfirePolicy = null, double? GraceSeconds = null);

/// <summary>一次发生的 UTC instant 与含偏移的当地显示。</summary>
/// <param name="Utc">UTC 时刻。</param>
/// <param name="Local">包含时区偏移的当地时刻。</param>
public sealed record ScheduleTime(DateTimeOffset Utc, DateTimeOffset Local);

/// <summary>日历规则验证及其未来发生。</summary>
/// <param name="Rule">规范化规则。</param>
/// <param name="Times">有界的未来时刻。</param>
public sealed record SchedulePreview(ScheduleRule Rule, IReadOnlyList<ScheduleTime> Times);
