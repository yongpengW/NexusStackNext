using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Costing.Application;

/// <summary>查询 Costing 对某次计划触发的持久接受结论；与消息交付和任务执行状态分开。</summary>
/// <param name="OccurrenceId">Scheduling 的发生标识。</param>
public sealed record GetScheduledCostReceipt(Guid OccurrenceId) : IQuery<ScheduledCostReceipt>;

/// <summary>Costing 按首次收到的意图形成的稳定结论；重投不重新解释已拒绝的发生。</summary>
/// <param name="OccurrenceId">发生标识。</param>
/// <param name="PlanId">来源计划。</param>
/// <param name="TriggerSequence">来源发生序号。</param>
/// <param name="ItemId">请求重算的成本对象。</param>
/// <param name="CreatedBy">原委托人。</param>
/// <param name="Decision">Accepted / Rejected。</param>
/// <param name="TaskId">接受时的本地任务标识；拒绝时为空。</param>
/// <param name="ErrorCode">拒绝的稳定错误码。</param>
/// <param name="ReceivedAt">Costing 在本地事务中观察到的接收时刻。</param>
public sealed record ScheduledCostReceipt(Guid OccurrenceId, long PlanId, long TriggerSequence, Guid ItemId, string CreatedBy,
    string Decision, Guid? TaskId, string? ErrorCode, DateTimeOffset ReceivedAt);
