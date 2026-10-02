using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Costing.Contracts;

/// <summary>成本输入某一版本的完整计算结果；消费者不得依赖到达顺序。</summary>
public sealed record CostCalculatedV1 : IntegrationEvent
{
    /// <summary>显式版本化的线路名称。</summary>
    public const string Name = "costing.cost-calculated.v1";
    /// <inheritdoc />
    public override string EventName => Name;
    /// <summary>跨上下文只传标识，不传实体引用。</summary>
    public required Guid ItemId { get; init; }
    /// <summary>Costing 输入版本，同一个对象严格递增。</summary>
    public required long CostRevision { get; init; }
    /// <summary>单位成本，最多四位小数；不包含 Pricing 的费率。</summary>
    public required decimal UnitCost { get; init; }
}
