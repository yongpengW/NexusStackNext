using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Pricing.Domain;

/// <summary>定价对象标识，由调用方提供。</summary>
public sealed record PriceId : StronglyTypedId<Guid>
{
    /// <summary>构造标识。</summary>
    /// <param name="value">非空标识。</param>
    public PriceId(Guid value) : base(value) { }
}

/// <summary>用于演示异步派生结果的定价聚合；不代表 PoS 正式计费模型。</summary>
public sealed class PriceQuote : AggregateRoot<PriceId>
{
    private PriceQuote(PriceId id) : base(id) { }

    /// <summary>单位成本。</summary>
    public decimal Cost { get; private set; }
    /// <summary>从售价扣除的费率。</summary>
    public decimal FeeRate { get; private set; }
    /// <summary>输入版本，与派生结果写入次数无关。</summary>
    public long InputRevision { get; private set; } = 1;
    /// <summary>最近已计算的输入版本；零表示尚无结果。</summary>
    public long CalculatedRevision { get; private set; }
    /// <summary>最近已计算的结果，是否最新由两个输入版本判定。</summary>
    public decimal? BreakEvenPrice { get; private set; }

    /// <summary>校验演示输入；最多四位小数，避免存储舍入改变请求含义。</summary>
    /// <param name="cost">单位成本。</param>
    /// <param name="feeRate">费率。</param>
    /// <returns>是否合法。</returns>
    public static bool IsValidInput(decimal cost, decimal feeRate) =>
        cost is >= 0 and <= 1_000_000_000m && feeRate is >= 0 and <= 0.99m
        && decimal.Round(cost, 4) == cost && decimal.Round(feeRate, 4) == feeRate;

    /// <summary>创建定价对象。</summary>
    /// <param name="id">调用方提供的标识。</param>
    /// <param name="cost">成本。</param>
    /// <param name="feeRate">费率。</param>
    /// <returns>聚合或校验错误。</returns>
    public static Result<PriceQuote> Create(PriceId id, decimal cost, decimal feeRate)
    {
        ArgumentNullException.ThrowIfNull(id);
        return IsValidInput(cost, feeRate)
            ? Result.Success(new PriceQuote(id) { Cost = cost, FeeRate = feeRate })
            : Result.Failure<PriceQuote>(InvalidInput);
    }

    /// <summary>更新输入；相同输入是空操作。</summary>
    /// <param name="cost">成本。</param>
    /// <param name="feeRate">费率。</param>
    /// <returns>变更结果。</returns>
    public Result UpdateCost(decimal cost, decimal feeRate)
    {
        if (!IsValidInput(cost, feeRate)) { return Result.Failure(InvalidInput); }
        if (Cost == cost && FeeRate == feeRate) { return Result.Success(); }
        Cost = cost;
        FeeRate = feeRate;
        InputRevision++;
        return Changed();
    }

    /// <summary>演示计算：成本除以一减费率，四位小数，远离零舍入。</summary>
    /// <param name="cost">已验证的成本。</param>
    /// <param name="feeRate">已验证的费率。</param>
    /// <returns>演示保本价。</returns>
    public static decimal Calculate(decimal cost, decimal feeRate)
    {
        if (!IsValidInput(cost, feeRate)) { throw new ArgumentOutOfRangeException(nameof(cost)); }
        return decimal.Round(cost / (1 - feeRate), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>保存当前输入的计算结果；旧输入不覆盖新结果。</summary>
    /// <param name="revision">计算使用的输入版本。</param>
    /// <param name="price">计算结果。</param>
    /// <returns>是否可以应用。</returns>
    public Result ApplyCalculation(long revision, decimal price)
    {
        if (revision != InputRevision) { return Result.Failure(new Error("pricing.superseded", "输入已经更新。")); }
        if (CalculatedRevision == revision && BreakEvenPrice == price) { return Result.Success(); }
        CalculatedRevision = revision;
        BreakEvenPrice = price;
        return Changed();
    }

    private static readonly Error InvalidInput = new("pricing.invalid_input", "成本须在 0 至 10 亿之间，费率须在 0 至 0.99 之间，均最多四位小数。");
}
