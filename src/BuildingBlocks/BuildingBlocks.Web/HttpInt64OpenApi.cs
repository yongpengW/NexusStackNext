using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NexusStackNext.BuildingBlocks.Web;

internal sealed class HttpInt64OpenApi : IOpenApiSchemaTransformer, IOpenApiDocumentTransformer
{
    internal const string DecimalFormat = "int64-decimal";
    internal const string OutputPattern = "^-?(0|[1-9][0-9]*)$";
    internal const string RangeDescription = "Signed 64-bit integer (-9223372036854775808 to 9223372036854775807), serialized as a decimal string to preserve JavaScript precision.";

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(long) || type == typeof(long?))
        {
            DescribeOutput(schema, type);
            if (context.ParameterDescription?.Source is { } source && (source == BindingSource.Path || source == BindingSource.Query))
            {
                schema.Format = null;
                schema.Pattern = null;
                schema.Description = "URL-bound signed 64-bit integer (-9223372036854775808 to 9223372036854775807). Send decimal text without JSON quotes; ASP.NET route/query binding rules apply.";
            }
        }
        // 自定义 scalar converter 的集合元素会被 exporter 生成为 true，
        // OpenAPI 不遍历这个空 schema；按公开 JsonTypeInfo 补全元素元数据。
        if (context.JsonTypeInfo.ElementType is { } element && (element == typeof(long) || element == typeof(long?)))
        {
            var item = new OpenApiSchema();
            DescribeOutput(item, element);
            if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Dictionary)
            {
                schema.AdditionalProperties = item;
            }
            else
            {
                schema.Items = item;
            }
        }
        return Task.CompletedTask;
    }

    private static void DescribeOutput(OpenApiSchema schema, Type type)
    {
        schema.Type = JsonSchemaType.String | (type == typeof(long?) ? JsonSchemaType.Null : 0);
        schema.Format = DecimalFormat;
        schema.Pattern = OutputPattern;
        schema.Description = $"{schema.Description} {RangeDescription}".Trim();
        schema.AnyOf = null;
        schema.OneOf = null;
        schema.AllOf = null;
        schema.Minimum = null;
        schema.Maximum = null;
    }

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        // CLR 类型可能同时用于输入和输出；共享 component 只能存一份定义。
        // 先生成统一的输出 schema，再为含 Int64 的 JSON 输入图创建独立 component。
        // 显式复制引用图并提前登记名字，递归 DTO 不会无限展开或污染输出。
        var projection = new InputSchemas(document);
        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in path.Operations?.Values.AsEnumerable() ?? [])
            {
                foreach (var (mediaType, media) in operation.RequestBody?.Content ?? new Dictionary<string, OpenApiMediaType>())
                {
                    if ((mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal)) && media.Schema is { } schema)
                    {
                        media.Schema = projection.Copy(schema);
                    }
                }
            }
        }
        return Task.CompletedTask;
    }

    private sealed class InputSchemas(OpenApiDocument document)
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

        public IOpenApiSchema Copy(IOpenApiSchema source)
        {
            if (!ContainsInt64(source, new HashSet<string>(StringComparer.Ordinal)))
            {
                return source;
            }
            if (source is OpenApiSchemaReference reference)
            {
                var id = reference.Reference.Id!;
                if (!_names.TryGetValue(id, out var name))
                {
                    name = id + "_Int64Input";
                    while (document.Components!.Schemas!.ContainsKey(name) || _names.ContainsValue(name))
                    {
                        name += "_";
                    }
                    _names.Add(id, name);
                    document.Components.Schemas.Add(name, Copy(document.Components.Schemas[id]));
                }
                return new OpenApiSchemaReference(name, document) { Description = reference.Description };
            }
            var copy = (OpenApiSchema)source.CreateShallowCopy();
            if (source.Format == DecimalFormat)
            {
                copy.Type = null;
                copy.Format = null;
                copy.Pattern = null;
                copy.Description = $"{source.Description?.Replace(RangeDescription, string.Empty, StringComparison.Ordinal)} Signed 64-bit input: decimal string (optional sign and leading zeros), or an exact integer JSON token. No whitespace, fraction or exponent. Range: -9223372036854775808 to 9223372036854775807. Prefer strings in JavaScript.".Trim();
                copy.OneOf =
                [
                    new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[+-]?[0-9]+$" },
                    new OpenApiSchema
                    {
                        Type = JsonSchemaType.Integer,
                        Format = "int64",
                        Minimum = long.MinValue.ToString(CultureInfo.InvariantCulture),
                        Maximum = long.MaxValue.ToString(CultureInfo.InvariantCulture),
                    },
                ];
                if ((source.Type & JsonSchemaType.Null) != 0)
                {
                    copy.OneOf.Add(new OpenApiSchema { Type = JsonSchemaType.Null });
                }
                return copy;
            }
            copy.Properties = source.Properties?.ToDictionary(pair => pair.Key, pair => Copy(pair.Value), StringComparer.Ordinal);
            copy.Items = source.Items is { } items ? Copy(items) : null;
            copy.AdditionalProperties = source.AdditionalProperties is { } extra ? Copy(extra) : null;
            copy.AllOf = source.AllOf?.Select(Copy).ToList();
            copy.AnyOf = source.AnyOf?.Select(Copy).ToList();
            copy.OneOf = source.OneOf?.Select(Copy).ToList();
            copy.Not = source.Not is { } not ? Copy(not) : null;
            return copy;
        }

        private bool ContainsInt64(IOpenApiSchema schema, HashSet<string> visited)
        {
            if (schema is OpenApiSchemaReference reference)
            {
                return reference.Reference.Id is { } id && visited.Add(id) &&
                    document.Components?.Schemas?.TryGetValue(id, out var target) == true && ContainsInt64(target, visited);
            }
            return schema.Format == DecimalFormat ||
                schema.Properties?.Values.Any(child => ContainsInt64(child, visited)) == true ||
                schema.Items is { } items && ContainsInt64(items, visited) ||
                schema.AdditionalProperties is { } extra && ContainsInt64(extra, visited) ||
                schema.AllOf?.Any(child => ContainsInt64(child, visited)) == true ||
                schema.AnyOf?.Any(child => ContainsInt64(child, visited)) == true ||
                schema.OneOf?.Any(child => ContainsInt64(child, visited)) == true ||
                schema.Not is { } not && ContainsInt64(not, visited);
        }
    }
}
