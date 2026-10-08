using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NexusStackNext.Costing.Application;

namespace NexusStackNext.Costing.Endpoints;

internal sealed record CostBatchJsonResult(AcceptCostBatch? Request, CostBatchValidation Validation);

internal static class CostBatchJson
{
    private static readonly HashSet<string> BatchFields = new(StringComparer.Ordinal) { "batchRequestId", "rows" };
    private static readonly HashSet<string> RowFields = new(StringComparer.Ordinal) { "sourceRow", "itemId", "expectedVersion", "purchaseCost", "freightCost" };

    public static async Task<CostBatchJsonResult> ReadAsync(HttpRequest request, JsonSerializerOptions options, CancellationToken token)
    {
        if (!request.HasJsonContentType()) { throw new BadHttpRequestException("批次须为 JSON。", 415); }
        // Bound actual bytes before creating any JSON document, including chunked bodies.
        const int maximum = 2 * 1024 * 1024;
        if (request.ContentLength > maximum) { throw new BadHttpRequestException("批次超过字节上限。", 413); }
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var received = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)bounded.Length)), token).ConfigureAwait(false);
            if (received == 0) { break; }
            bounded.Write(buffer, 0, received);
            if (bounded.Length > maximum) { throw new BadHttpRequestException("批次超过字节上限。", 413); }
        }
        var errors = new List<CostBatchFieldError>(20);
        var count = 0;
        void Add(int row, string field, string code)
        {
            count++;
            if (errors.Count < 20) { errors.Add(new(row, field, code)); }
        }
        void Schema(JsonElement element, HashSet<string> fields, int position)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!fields.Contains(property.Name)) { Add(position, "row", "unknown_field"); }
                else if (!seen.Add(property.Name)) { Add(position, property.Name, "duplicate_field"); }
            }
        }
        try
        {
            using var document = JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, (int)bounded.Length), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Add(0, "batch", "invalid"); return new(null, new(count, errors)); }
            Schema(root, BatchFields, 0);
            var batchId = root.TryGetProperty("batchRequestId", out var identity) && identity.ValueKind == JsonValueKind.String && identity.TryGetGuid(out var parsedId)
                ? parsedId : Guid.Empty;
            if (batchId == Guid.Empty) { Add(0, "batchRequestId", "invalid"); }
            if (!root.TryGetProperty("rows", out var originalRows) || originalRows.ValueKind != JsonValueKind.Array || originalRows.GetArrayLength() is < 1 or > 5000)
            {
                Add(0, "rows", "invalid");
                return new(null, new(count, errors));
            }
            var rows = new List<CostBatchInput>(originalRows.GetArrayLength());
            var index = 0;
            foreach (var element in originalRows.EnumerateArray())
            {
                var position = ++index;
                if (element.ValueKind != JsonValueKind.Object) { Add(position, "row", "invalid"); continue; }
                var unparsed = new HashSet<string>(StringComparer.Ordinal);
                T Read<T>(string field, T fallback, bool required = true) where T : struct
                {
                    if (!element.TryGetProperty(field, out var value))
                    {
                        if (required) { Add(position, field, "required"); unparsed.Add(field); }
                        return fallback;
                    }
                    try { return value.Deserialize<T>(options); }
                    catch (JsonException)
                    {
                        Add(position, field, "invalid");
                        unparsed.Add(field);
                        return fallback;
                    }
                }
                var sourceRow = Read("sourceRow", index, required: false);
                if (sourceRow > 0) { position = sourceRow; }
                Schema(element, RowFields, position);
                var row = new CostBatchInput(sourceRow, Read("itemId", Guid.Empty),
                    Read("expectedVersion", 0L), Read("purchaseCost", 0m), Read("freightCost", 0m));
                rows.Add(row);
                foreach (var error in CostBatchValidation.CheckRow(row, index))
                {
                    if (unparsed.Contains(error.Field)
                        || (error.Field == "totalCost" && (unparsed.Contains("purchaseCost") || unparsed.Contains("freightCost")))) { continue; }
                    Add(error.SourceRow, error.Field, error.Code);
                }
            }
            var accepted = new AcceptCostBatch(batchId, rows);
            return new(count == 0 ? accepted : null, new(count, errors));
        }
        catch (JsonException)
        {
            return new(null, new(1, [new(0, "batch", "invalid_json")]));
        }
    }
}
