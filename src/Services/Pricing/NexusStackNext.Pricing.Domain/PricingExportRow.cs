namespace NexusStackNext.Pricing.Domain;

/// <summary>冻结时观察到的一行精确报价；不随报价后续修改而变化。</summary>
/// <param name="ItemId">报价对象标识。</param>
/// <param name="Version">报价聚合版本。</param>
/// <param name="Cost">单位成本。</param>
/// <param name="FeeRate">费率。</param>
/// <param name="InputRevision">输入版本。</param>
/// <param name="CalculatedRevision">已计算版本。</param>
/// <param name="CostingRevision">上游成本版本。</param>
/// <param name="BreakEvenPrice">最近计算结果；空表示尚未计算。</param>
public sealed record PricingExportRow(PriceId ItemId, long Version, decimal Cost, decimal FeeRate,
    long InputRevision, long CalculatedRevision, long CostingRevision, decimal? BreakEvenPrice)
{
    /// <summary>冻结时结果的新旧状态：Pending、Stale 或 Current。</summary>
    public string CalculationState => CalculatedRevision == 0 ? "Pending"
        : CalculatedRevision < InputRevision ? "Stale" : "Current";
}
