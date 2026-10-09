using System.Text;
using NexusStackNext.Pricing.Domain;

namespace NexusStackNext.Pricing.Application;

/// <summary>固定列报价 CSV v1 的精确文本输出。</summary>
public static class PricingCsvV1
{
    /// <summary>将冻结行按原顺序写为 UTF-8 CSV；不读取活报价或缓存。</summary>
    /// <param name="rows">已经冻结的有界行。</param>
    /// <param name="output">调用方持有的输出流。</param>
    /// <param name="maxBytes">实际字节上限；至多 32 MiB。</param>
    /// <param name="cancellationToken">生成取消。</param>
    /// <returns>完整写入。</returns>
    public static async Task WriteAsync(IReadOnlyList<PricingExportRow> rows, Stream output, long maxBytes = 33_554_432,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(output);
        if (maxBytes is < 1 or > 33_554_432) { throw new ArgumentOutOfRangeException(nameof(maxBytes)); }
        const string header = "ItemId,Version,Cost,FeeRate,InputRevision,CalculatedRevision,CostingRevision,BreakEvenPrice,CalculationState\r\n";
        long written = Encoding.UTF8.GetByteCount(header);
        if (written > maxBytes) { throw new InvalidDataException("pricing.export.output_limit"); }
        await using var writer = new StreamWriter(output, new UTF8Encoding(false, true), 16_384, leaveOpen: true);
        await writer.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            // 固定列只含 Guid、精确数值和白名单状态；没有需要 RFC CSV 引号转义的字符。
            var line = FormattableString.Invariant($"{row.ItemId.Value:D},{row.Version},{row.Cost:0.0000},{row.FeeRate:0.0000},{row.InputRevision},{row.CalculatedRevision},{row.CostingRevision},{row.BreakEvenPrice:0.0000},{row.CalculationState}\r\n");
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes > maxBytes - written) { throw new InvalidDataException("pricing.export.output_limit"); }
            written += bytes;
            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
