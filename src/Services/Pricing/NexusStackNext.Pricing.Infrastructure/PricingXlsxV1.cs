using System.Globalization;
using NexusStackNext.BuildingBlocks.Infrastructure.Exports;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Infrastructure;

/// <summary>固定单工作表报价 XLSX v1；精确字段写成文本。</summary>
public static class PricingXlsxV1
{
    private static readonly string[] Headers = ["ItemId", "Version", "Cost", "FeeRate", "InputRevision", "CalculatedRevision", "CostingRevision", "BreakEvenPrice", "CalculationState"];
    /// <summary>生成并检查完整报价工作簿，不关闭调用方的流。</summary>
    /// <param name="rows">最多五千条固定报价。</param>
    /// <param name="output">调用方拥有的可读写空流。</param>
    /// <param name="maxBytes">实际 ZIP 字节上限。</param>
    /// <param name="cancellationToken">执行预算。</param>
    /// <returns>完整工作簿。</returns>
    public static async Task WriteAsync(IReadOnlyList<PricingExportRow> rows, Stream output, long maxBytes = 33_554_432, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        try { await TextWorkbook.WriteAsync("Quotes", Headers, rows.Select(Values).ToArray(), output, maxBytes, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException error) { throw new InvalidDataException(error.Message.Replace("export.", "pricing.export.", StringComparison.Ordinal), error); }
    }

    private static string[] Values(PricingExportRow row) => [row.ItemId.Value.ToString("D"), row.Version.ToString(CultureInfo.InvariantCulture),
        row.Cost.ToString("0.0000", CultureInfo.InvariantCulture), row.FeeRate.ToString("0.0000", CultureInfo.InvariantCulture),
        row.InputRevision.ToString(CultureInfo.InvariantCulture), row.CalculatedRevision.ToString(CultureInfo.InvariantCulture),
        row.CostingRevision.ToString(CultureInfo.InvariantCulture), row.BreakEvenPrice?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "", row.CalculationState];
}
