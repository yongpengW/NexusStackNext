using Cronos;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Infrastructure;

/// <summary>Cronos + 显式 IANA 时区的日历计算，不读取系统当地时区。</summary>
public sealed class CronScheduleCalendar : IScheduleCalendar
{
    /// <inheritdoc />
    public Result<DateTimeOffset> NextOccurrence(ScheduleRule rule, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var preview = Preview(new ScheduleRuleInput(rule.Kind, rule.Expression, rule.TimeZoneId, rule.Day, rule.Hour,
            rule.Minute, rule.IntervalSeconds, rule.CronFieldCount, rule.MisfirePolicy, rule.GraceSeconds), after, 1);
        return preview.IsSuccess ? Result.Success(preview.Value.Times[0].Utc) : Result.Failure<DateTimeOffset>(preview.Error);
    }

    /// <inheritdoc />
    public Result<SchedulePreview> Preview(ScheduleRuleInput input, DateTimeOffset after, int count)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (count is < 1 or > 10 || after.UtcDateTime.Year > 9994)
        {
            return Failure("preview.invalid", "预览数量必须在 1 到 10 之间，起点须留出五年计算窗口。");
        }
        if (input.Kind == "Interval")
        {
            if (input.IntervalSeconds is not { } seconds || !double.IsFinite(seconds) || seconds is < 1 or > 31622400
                || input.Expression is not null || input.TimeZoneId is not null || input.CronFieldCount is not null
                || input.Day is not null || input.Hour is not null || input.Minute is not null
                || input.MisfirePolicy is not null || input.GraceSeconds is not null)
            {
                return Failure("rule.invalid", "Interval 必须只指定 1 秒到 366 天的间隔，不能包含日历和漏跑策略字段。");
            }
            var interval = ScheduleRule.NormalizeInterval(seconds);
            var cursor = after.ToUniversalTime();
            var horizon = cursor.AddYears(5);
            var occurrences = new List<ScheduleTime>();
            for (var index = 0; index < count && horizon - cursor >= interval; index++)
            {
                cursor += interval;
                occurrences.Add(new ScheduleTime(cursor, cursor));
            }
            return Result.Success(new SchedulePreview(ScheduleRule.FixedInterval(interval), occurrences));
        }
        if (input.IntervalSeconds is not null) { return Failure("rule.invalid", "固定间隔不能与日历规则混用。"); }
        var policy = input.MisfirePolicy ?? "FireOnce";
        var grace = input.GraceSeconds ?? 30;
        if (policy is not ("FireOnce" or "Skip") || !double.IsFinite(grace) || grace is < 1 or > 3600)
        {
            return Failure("misfire.invalid", "漏跑策略必须为 FireOnce 或 Skip，宽限在 1 秒到 1 小时之间。");
        }
        if (string.IsNullOrWhiteSpace(input.TimeZoneId) || input.TimeZoneId.Length > 128)
        {
            return Failure("timezone.invalid", "必须指定有效的 IANA 时区。");
        }
        var zoneId = input.TimeZoneId == "UTC" ? "Etc/UTC" : input.TimeZoneId;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException) { return Failure("timezone.invalid", "无法解析指定的 IANA 时区。"); }
        catch (InvalidTimeZoneException) { return Failure("timezone.invalid", "指定的时区数据不可用。"); }
        if (!zone.HasIanaId) { return Failure("timezone.invalid", "必须使用 IANA 时区标识。"); }
        ScheduleRule rule;
        CronExpression[] expressions;
        if (input.Kind == "Cron")
        {
            if (string.IsNullOrWhiteSpace(input.Expression) || input.Expression.Length > 256
                || input.Day is not null || input.Hour is not null || input.Minute is not null)
            {
                return Failure("rule.invalid", "Cron 表达式长度最多 256 字符，不能混入月度日期字段。");
            }
            var fields = input.Expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is not (5 or 6)) { return Failure("cron.invalid", "Cron 必须恰好包含五或六个字段。"); }
            if (input.CronFieldCount is { } fieldCount && fieldCount != fields.Length) { return Failure("cron.invalid", "显式字段数必须与表达式一致。"); }
            var expression = string.Join(' ', fields).ToUpperInvariant();
            try { expressions = [CronExpression.Parse(expression, fields.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard)]; }
            catch (CronFormatException) { return Failure("cron.invalid", "Cron 表达式不受支持或字段值无效。"); }
            catch (MissingSeedException) { return Failure("cron.invalid", "不支持需要随机种子的 H 表达式。"); }
            rule = new ScheduleRule("Cron", expression, zoneId, fields.Length, MisfirePolicy: policy, GraceSeconds: grace);
        }
        else if (input.Kind == "MonthlyDay")
        {
            if (input.Expression is not null || input.CronFieldCount is not null || input.Day is not (>= 1 and <= 31)
                || input.Hour is not (>= 0 and <= 23) || input.Minute is not (>= 0 and <= 59))
            {
                return Failure("rule.invalid", "MonthlyDay 必须指定日 1 到 31、小时 0 到 23 和分钟 0 到 59，不能包含 Cron 表达式。");
            }
            expressions = input.Day switch
            {
                <= 28 => [CronExpression.Parse(FormattableString.Invariant($"{input.Minute} {input.Hour} {input.Day} * *"))],
                31 => [CronExpression.Parse(FormattableString.Invariant($"{input.Minute} {input.Hour} L * *"))],
                _ => [CronExpression.Parse(FormattableString.Invariant($"{input.Minute} {input.Hour} {input.Day} 1,3-12 *")),
                    CronExpression.Parse(FormattableString.Invariant($"{input.Minute} {input.Hour} L 2 *"))],
            };
            rule = new ScheduleRule("MonthlyDay", null, zoneId, null, input.Day, input.Hour, input.Minute, policy, grace);
        }
        else { return Failure("rule.invalid", "必须明确指定 Interval、Cron 或 MonthlyDay 规则。"); }

        var until = after.ToUniversalTime().AddYears(5);
        var times = expressions.SelectMany(cron => cron.GetOccurrences(after.ToUniversalTime(), until, zone, fromInclusive: false, toInclusive: true).Take(count))
            .Distinct().Order().Take(count).Select(time => new ScheduleTime(time.ToUniversalTime(), TimeZoneInfo.ConvertTime(time, zone))).ToArray();
        return times.Length > 0
            ? Result.Success(new SchedulePreview(rule, times))
            : Failure("next_run.unavailable", "五年前瞻窗口内没有发生时刻。");
    }

    private static Result<SchedulePreview> Failure(string suffix, string message) =>
        Result.Failure<SchedulePreview>(new Error($"scheduling.{suffix}", message));
}
