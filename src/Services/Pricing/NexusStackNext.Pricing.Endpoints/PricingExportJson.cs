using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;

namespace NexusStackNext.Pricing.Endpoints;

internal static class PricingExportJson
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "requestId", "itemIds", "calculationState", "format", "formatVersion", "columnSetVersion" };

    public static async Task<Result<AcceptPricingExport>> ReadAsync(HttpRequest request, CancellationToken token)
    {
        if (!request.HasJsonContentType()) { throw new BadHttpRequestException("导出请求须为 JSON。", 415); }
        const int maximum = 262_144;
        if (request.ContentLength > maximum) { throw new BadHttpRequestException("导出请求超过字节上限。", 413); }
        using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        readBudget.CancelAfter(TimeSpan.FromSeconds(15));
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)body.Length)), readBudget.Token).ConfigureAwait(false);
                if (read == 0) { break; }
                body.Write(buffer, 0, read);
                if (body.Length > maximum) { throw new BadHttpRequestException("导出请求超过字节上限。", 413); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new BadHttpRequestException("导出请求读取超过时限。", 408); }
        try
        {
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return Invalid(); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) { if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) { return Invalid(); } }
            if (!root.TryGetProperty("requestId", out var identity) || identity.ValueKind != JsonValueKind.String || !identity.TryGetGuid(out var requestId) || requestId == Guid.Empty
                || !root.TryGetProperty("itemIds", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 5000) { return Invalid(); }
            var ids = new Guid[items.GetArrayLength()];
            var index = 0;
            foreach (var value in items.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty) { return Invalid(); }
                ids[index++] = id;
            }
            var state = "Any";
            if (root.TryGetProperty("calculationState", out var filter))
            {
                if (filter.ValueKind != JsonValueKind.String || filter.GetString() is not ("Any" or "Pending" or "Stale" or "Current")) { return Invalid(); }
                state = filter.GetString()!;
            }
            foreach (var field in new[] { "formatVersion", "columnSetVersion" })
            {
                if (root.TryGetProperty(field, out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)) { return Invalid(); }
            }
            var format = "csv";
            if (root.TryGetProperty("format", out var selectedFormat))
            {
                if (selectedFormat.ValueKind != JsonValueKind.String || selectedFormat.GetString() is not ("csv" or "xlsx")) { return Invalid(); }
                format = selectedFormat.GetString()!;
            }
            return Result.Success(new AcceptPricingExport(requestId, Array.AsReadOnly(ids), state, Format: format));
        }
        catch (JsonException) { return Invalid(); }
    }

    private static Result<AcceptPricingExport> Invalid() => Result.Failure<AcceptPricingExport>(new Error("pricing.export.invalid", "导出请求或白名单筛选无效。"));
}
