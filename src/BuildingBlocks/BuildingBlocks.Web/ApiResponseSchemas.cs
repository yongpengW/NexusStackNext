using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace NexusStackNext.BuildingBlocks.Web;

/// <summary>供边缘在后端文档不可用时仍能声明自身 HTTP 错误契约。</summary>
public static class ApiResponseSchemas
{
    /// <summary>生成与 HTTP Int64 线格式一致的 ProblemDetails JSON Schema。</summary>
    /// <returns>独立的 schema，可直接加入聚合文档。</returns>
    public static JsonNode ProblemDetails() => JsonSerializerOptions.Web.GetJsonSchemaAsNode(typeof(ApiProblemDetails), new JsonSchemaExporterOptions
    {
        TransformSchemaNode = static (context, schema) => context.TypeInfo.Type == typeof(long)
            ? new JsonObject
            {
                ["type"] = "string",
                ["format"] = HttpInt64OpenApi.DecimalFormat,
                ["pattern"] = HttpInt64OpenApi.OutputPattern,
                ["description"] = HttpInt64OpenApi.RangeDescription,
            }
            : schema,
    });
}
