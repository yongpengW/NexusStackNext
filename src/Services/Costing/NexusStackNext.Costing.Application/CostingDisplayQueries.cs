using NexusStackNext.BuildingBlocks.Application.Messaging;

namespace NexusStackNext.Costing.Application;

/// <summary>读取允许固定 TTL 旧值的成本展示快照；编辑和任务流程继续使用权威读取。</summary>
/// <param name="ItemId">成本核算对象标识。</param>
public sealed record GetCostSheetDisplay(Guid ItemId) : IQuery<CostSheetView>;
