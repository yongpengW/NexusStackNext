using NexusStackNext.BuildingBlocks.Application.Tasks;

namespace NexusStackNext.Costing.Application;

/// <summary>Costing 的有限计算策略，由所属宿主配置。</summary>
public sealed record CostingTaskOptions : DurableTaskOptions;
