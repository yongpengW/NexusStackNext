using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Costing.Application;

/// <summary>查询计算结果的投递情况；Delivered 只表示 broker 已确认接收。</summary>
/// <param name="TaskId">同时作为事件标识的成本任务标识。</param>
public sealed record GetCostDelivery(Guid TaskId) : IQuery<CostDeliveryStatus>;

/// <summary>成本计算成功后的独立投递状态。</summary>
/// <param name="TaskId">成本任务及消息标识。</param>
/// <param name="State">Pending、Delivered 或 DeadLettered。</param>
/// <param name="Attempts">本轮失败次数。</param>
/// <param name="NextAttemptAt">自动重试时刻。</param>
/// <param name="DeadLetteredAt">人工重试的并发凭据。</param>
public sealed record CostDeliveryStatus(Guid TaskId, string State, int Attempts, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeadLetteredAt);

/// <summary>显式重投失败的成本事件，保留消息 ID 以便下游去重。</summary>
/// <param name="TaskId">事件标识。</param>
/// <param name="ExpectedDeadLetteredAt">查询到的失败时刻，防止旧操作重复开放预算。</param>
public sealed record RetryCostDelivery(Guid TaskId, DateTimeOffset ExpectedDeadLetteredAt) : ICommand<CostDeliveryStatus>;
