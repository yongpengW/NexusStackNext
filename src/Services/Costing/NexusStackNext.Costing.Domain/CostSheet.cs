using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Costing.Domain;

/// <summary>成本核算对象标识，由调用方提供。</summary>
public sealed record CostId : StronglyTypedId<Guid>
{
    /// <summary>构造标识。</summary>
    /// <param name="value">非空标识。</param>
    public CostId(Guid value) : base(value) { }
}

/// <summary>用于演示异步派生结果的成本核算聚合；不代表 PoS 正式计费模型。</summary>
public sealed class CostSheet : AuditedAggregateRoot<CostId>
{
    private CostSheet(CostId id) : base(id) { }

    /// <summary>单位采购成本。</summary>
    public decimal PurchaseCost { get; private set; }
    /// <summary>分摊到单位的运费。</summary>
    public decimal FreightCost { get; private set; }
    /// <summary>输入版本，与派生结果写入次数无关。</summary>
    public long InputRevision { get; private set; } = 1;
    /// <summary>最近已计算的输入版本；零表示尚无结果。</summary>
    public long CalculatedRevision { get; private set; }
    /// <summary>最近已计算的结果，是否最新由两个输入版本判定。</summary>
    public decimal? UnitCost { get; private set; }

    /// <summary>校验演示输入；最多四位小数，避免存储舍入改变请求含义。</summary>
    /// <param name="cost">单位成本。</param>
    /// <param name="freightCost">单位运费。</param>
    /// <returns>是否合法。</returns>
    public static bool IsValidInput(decimal cost, decimal freightCost) =>
        IsValidAmount(cost) && IsValidAmount(freightCost) && cost + freightCost <= 1_000_000_000m;

    /// <summary>单个成本金额非负、不超过十亿且最多四位小数。</summary>
    /// <param name="amount">待验证金额。</param>
    /// <returns>是否符合成本金额规则。</returns>
    public static bool IsValidAmount(decimal amount) =>
        amount is >= 0 and <= 1_000_000_000m && decimal.Round(amount, 4) == amount;

    /// <summary>创建成本核算对象。</summary>
    /// <param name="id">调用方提供的标识。</param>
    /// <param name="cost">成本。</param>
    /// <param name="freightCost">单位运费。</param>
    /// <returns>聚合或校验错误。</returns>
    public static Result<CostSheet> Create(CostId id, decimal cost, decimal freightCost)
    {
        ArgumentNullException.ThrowIfNull(id);
        return IsValidInput(cost, freightCost)
            ? Result.Success(new CostSheet(id) { PurchaseCost = cost, FreightCost = freightCost })
            : Result.Failure<CostSheet>(InvalidInput);
    }

    /// <summary>更新输入；相同输入是空操作。</summary>
    /// <param name="cost">成本。</param>
    /// <param name="freightCost">单位运费。</param>
    /// <returns>变更结果。</returns>
    public Result UpdateCost(decimal cost, decimal freightCost)
    {
        if (!IsValidInput(cost, freightCost)) { return Result.Failure(InvalidInput); }
        if (PurchaseCost == cost && FreightCost == freightCost) { return Result.Success(); }
        PurchaseCost = cost;
        FreightCost = freightCost;
        InputRevision++;
        return Changed();
    }

    /// <summary>演示计算：单位采购成本加单位运费。</summary>
    /// <param name="cost">已验证的成本。</param>
    /// <param name="freightCost">已验证的单位运费。</param>
    /// <returns>演示单位成本。</returns>
    public static decimal Calculate(decimal cost, decimal freightCost)
    {
        if (!IsValidInput(cost, freightCost)) { throw new ArgumentOutOfRangeException(nameof(cost)); }
        return cost + freightCost;
    }

    /// <summary>保存当前输入的计算结果；旧输入不覆盖新结果。</summary>
    /// <param name="revision">计算使用的输入版本。</param>
    /// <param name="unitCost">计算结果。</param>
    /// <returns>是否可以应用。</returns>
    public Result ApplyCalculation(long revision, decimal unitCost)
    {
        if (revision != InputRevision) { return Result.Failure(new Error("costing.superseded", "输入已经更新。")); }
        if (CalculatedRevision == revision && UnitCost == unitCost) { return Result.Success(); }
        CalculatedRevision = revision;
        UnitCost = unitCost;
        return Changed();
    }

    private static readonly Error InvalidInput = new("costing.invalid_input", "采购成本与运费须非负、合计不超过 10 亿，均最多四位小数。");
}
