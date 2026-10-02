using NexusStackNext.BuildingBlocks.Application.Tasks;

namespace NexusStackNext.Pricing.Application;

/// <summary>Pricing 的有限计算策略，由所属宿主配置。</summary>
public sealed record PricingTaskOptions : DurableTaskOptions;
