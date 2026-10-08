using NexusStackNext.Costing.Domain;

namespace NexusStackNext.Costing.Application;

/// <summary>安全的原始行定位，不回显输入值或自由文本字段名。</summary>
/// <param name="SourceRow">原始行；零表示批次本身。</param>
/// <param name="Field">固定字段名称。</param>
/// <param name="Code">固定字段错误码。</param>
public sealed record CostBatchFieldError(int SourceRow, string Field, string Code);

/// <summary>完整计数与有限错误样本。</summary>
/// <param name="ErrorCount">全部静态错误数量。</param>
/// <param name="Errors">前二十条错误。</param>
public sealed record CostBatchValidation(int ErrorCount, IReadOnlyList<CostBatchFieldError> Errors)
{
    /// <summary>在去重前校验全部原始行；HTTP 与 ISender 使用同一规则。</summary>
    /// <param name="request">原始批次。</param>
    /// <returns>稳定错误计数和安全定位。</returns>
    public static CostBatchValidation Check(AcceptCostBatch request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = 0;
        var errors = new List<CostBatchFieldError>(20);
        void Add(int sourceRow, string field)
        {
            count++;
            if (errors.Count < 20) { errors.Add(new(sourceRow, field, "invalid")); }
        }
        if (request.BatchRequestId == Guid.Empty) { Add(0, "batchRequestId"); }
        if (request.Rows is null || request.Rows.Count is < 1 or > 5000)
        {
            Add(0, "rows");
            return new(count, errors);
        }
        for (var index = 0; index < request.Rows.Count; index++)
        {
            var row = request.Rows[index];
            if (row is null) { Add(index + 1, "row"); continue; }
            var rowErrors = CheckRow(row, index + 1);
            count += rowErrors.Count;
            errors.AddRange(rowErrors.Take(20 - errors.Count));
        }
        return new(count, errors);
    }

    /// <summary>校验一行的全部业务字段；HTTP 可与逐字段解析错误合并而不丢失原始行。</summary>
    /// <param name="row">原始行。</param>
    /// <param name="fallbackPosition">行号无效时的原始数组位置。</param>
    /// <returns>全部安全字段错误。</returns>
    public static IReadOnlyList<CostBatchFieldError> CheckRow(CostBatchInput row, int fallbackPosition)
    {
        ArgumentNullException.ThrowIfNull(row);
        var position = row.SourceRow > 0 ? row.SourceRow : fallbackPosition;
        var errors = new List<CostBatchFieldError>(6);
        void Add(string field) => errors.Add(new(position, field, "invalid"));
        if (row.SourceRow <= 0) { Add("sourceRow"); }
        if (row.ItemId == Guid.Empty) { Add("itemId"); }
        if (row.ExpectedVersion < 0) { Add("expectedVersion"); }
        var costValid = CostSheet.IsValidAmount(row.PurchaseCost);
        var freightValid = CostSheet.IsValidAmount(row.FreightCost);
        if (!costValid) { Add("purchaseCost"); }
        if (!freightValid) { Add("freightCost"); }
        if (costValid && freightValid && !CostSheet.IsValidInput(row.PurchaseCost, row.FreightCost)) { Add("totalCost"); }
        return errors;
    }
}
