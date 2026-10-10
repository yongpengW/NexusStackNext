using System.Text.Json;
using System.Text.Json.Serialization;
using NexusStackNext.Auditing.Application;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Endpoints;

internal static class AuditExportJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal static async Task<Result<AcceptAuditExport>> ReadAsync(HttpRequest request, CancellationToken token)
    {
        const int maximum = 16_384;
        if (!request.HasJsonContentType()) { throw new BadHttpRequestException("调查导出请求须为 JSON。", 415); }
        if (request.ContentLength > maximum) { throw new BadHttpRequestException("调查导出请求超过字节上限。", 413); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)body.Length)), budget.Token).ConfigureAwait(false);
                if (read == 0) { break; }
                body.Write(buffer, 0, read);
                if (body.Length > maximum) { throw new BadHttpRequestException("调查导出请求超过字节上限。", 413); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new BadHttpRequestException("调查导出请求读取超过时限。", 408); }
        try
        {
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement)) { return Invalid(); }
            var parsed = document.RootElement.Deserialize<AcceptAuditExport>(Options);
            return parsed is null ? Invalid() : Result.Success(parsed);
        }
        catch (JsonException) { return Invalid(); }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !seen.Add(property.Name) || HasDuplicateProperties(property.Value));
    }

    private static Result<AcceptAuditExport> Invalid() => Result.Failure<AcceptAuditExport>(new Error("auditing.export.invalid", "调查导出请求或筛选字段无效。"));
}
