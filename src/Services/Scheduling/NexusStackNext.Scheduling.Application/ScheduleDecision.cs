using NexusStackNext.Scheduling.Domain.Tasks;

namespace NexusStackNext.Scheduling.Application;

/// <summary>一次已提交的调度决定；跳过也是事实，但不产生业务发生或消息。</summary>
/// <param name="DecisionId">事务重试期间保持不变的决定标识。</param>
/// <param name="PlanId">所属计划。</param>
/// <param name="PlanVersion">决定提交后的聚合版本。</param>
/// <param name="ScheduleRevision">当时的规则修订。</param>
/// <param name="Rule">当时规则的不可变快照。</param>
/// <param name="Kind">Triggered / Coalesced / Skipped。</param>
/// <param name="ScheduledAt">最早尚未处理的计划时刻。</param>
/// <param name="ObservedAt">本轮唯一的处理时刻。</param>
/// <param name="NextRunAt">推进后的下次时刻。</param>
/// <param name="OccurrenceId">产生业务意图时关联的发生标识；跳过为空。</param>
public sealed record ScheduleDecision(Guid DecisionId, long PlanId, long PlanVersion, long ScheduleRevision,
    ScheduleRule Rule, string Kind, DateTimeOffset ScheduledAt, DateTimeOffset ObservedAt, DateTimeOffset NextRunAt, Guid? OccurrenceId);

/// <summary>有界调度决定历史。</summary>
/// <param name="Items">当前页。</param>
/// <param name="Total">该计划总决定数。</param>
public sealed record ScheduleDecisionPage(IReadOnlyList<ScheduleDecision> Items, long Total);
