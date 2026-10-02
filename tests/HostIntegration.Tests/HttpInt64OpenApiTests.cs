using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusStackNext.BuildingBlocks.Web;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class HttpInt64OpenApiTests
{
    internal static async Task AssertHostDocumentsAsync(HttpClient platform, HttpClient costing, HttpClient pricing, HttpClient gateway)
    {
        foreach (var (client, path, versionedWrite) in new[]
        {
            (platform, "/api/scheduling/tasks", "/api/scheduling/tasks/{id}/pause"),
            (costing, "/api/costing/items/{itemId}", "/api/costing/cost"),
            (pricing, "/api/pricing/items/{itemId}", "/api/pricing/fee"),
        })
        {
            var source = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
            AssertEndpointSchemas(source, path, versionedWrite);
        }
        var own = await gateway.GetFromJsonAsync<JsonElement>(new Uri("/openapi/gateway.json", UriKind.Relative));
        AssertSchemasAndReferences(own);
        var aggregate = await gateway.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        AssertEndpointSchemas(aggregate, "/api/scheduling/tasks", "/api/scheduling/tasks/{id}/pause");
        AssertEndpointSchemas(aggregate, "/api/costing/items/{itemId}", "/api/costing/cost");
        AssertEndpointSchemas(aggregate, "/api/pricing/items/{itemId}", "/api/pricing/fee");
        AssertOutput(aggregate.GetProperty("components").GetProperty("schemas").GetProperty("EdgeProblem")
            .GetProperty("properties").GetProperty("timestamp"), nullable: false);
    }

    private static void AssertEndpointSchemas(JsonElement document, string readPath, string writePath)
    {
        AssertSchemasAndReferences(document);
        var paths = document.GetProperty("paths");
        var response = Resolve(document, paths.GetProperty(readPath).GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        AssertOutput(response.GetProperty("properties").GetProperty("timestamp"), nullable: false);
        if (readPath == "/api/scheduling/tasks")
        {
            AssertOutput(response.GetProperty("properties").GetProperty("total"), nullable: false);
        }
        else
        {
            var data = Resolve(document, response.GetProperty("properties").GetProperty("data"));
            AssertOutput(data.GetProperty("properties").GetProperty("version"), nullable: false);
        }
        var input = Resolve(document, paths.GetProperty(writePath).GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        AssertInput(input.GetProperty("properties").GetProperty("expectedVersion"), nullable: false);
    }

    private static void AssertSchemasAndReferences(JsonElement document)
    {
        var integers = 0;
        var references = 0;
        Visit(document);
        Assert.True(integers > 0, "No Int64 output schemas were found.");
        Assert.True(references > 0, "No schema references were checked.");

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { Visit(item); }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("$ref", out var reference))
                {
                    var target = document;
                    Assert.StartsWith("#/", reference.GetString(), StringComparison.Ordinal);
                    foreach (var segment in reference.GetString()![2..].Split('/'))
                    {
                        target = target.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                    }
                    references++;
                }
                if (element.TryGetProperty("format", out var format) && format.GetString() == "int64-decimal")
                {
                    AssertOutput(element, element.GetProperty("type").ValueKind == JsonValueKind.Array);
                    integers++;
                }
                foreach (var property in element.EnumerateObject()) { Visit(property.Value); }
            }
        }
    }

    [Fact]
    public async Task SharedAndRecursiveDtos_HaveDistinctAccurateRequestAndResponseSchemas()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddOpenApi();
        builder.Services.AddApiResponseContract();
        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapPost("/probe", (SharedModel input) => TypedResults.Ok(input));
        app.MapGet("/probe/{id}", (long id, long? expectedVersion) => TypedResults.Ok(new SharedModel(id, expectedVersion, [], null, [], [])));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var post = document.GetProperty("paths").GetProperty("/probe").GetProperty("post");
        var request = Resolve(document, post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var response = Resolve(document, post.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        AssertOutput(response.GetProperty("properties").GetProperty("id"), nullable: false);
        AssertOutput(response.GetProperty("properties").GetProperty("version"), nullable: true);
        AssertOutput(Resolve(document, response.GetProperty("properties").GetProperty("values")).GetProperty("items"), nullable: false);
        AssertInput(request.GetProperty("properties").GetProperty("id"), nullable: false);
        AssertInput(request.GetProperty("properties").GetProperty("version"), nullable: true);
        AssertInput(Resolve(document, request.GetProperty("properties").GetProperty("values")).GetProperty("items"), nullable: false);
        AssertOutput(response.GetProperty("properties").GetProperty("totals").GetProperty("additionalProperties"), nullable: true);
        AssertInput(request.GetProperty("properties").GetProperty("totals").GetProperty("additionalProperties"), nullable: true);
        AssertOutput(response.GetProperty("properties").GetProperty("optionalValues").GetProperty("items"), nullable: true);
        AssertInput(request.GetProperty("properties").GetProperty("optionalValues").GetProperty("items"), nullable: true);
        var requestChild = Resolve(document, request.GetProperty("properties").GetProperty("child"));
        var responseChild = Resolve(document, response.GetProperty("properties").GetProperty("child"));
        AssertInput(requestChild.GetProperty("properties").GetProperty("id"), nullable: false);
        AssertOutput(responseChild.GetProperty("properties").GetProperty("id"), nullable: false);
        var parameters = document.GetProperty("paths").GetProperty("/probe/{id}").GetProperty("get").GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(2, parameters.Length);
        foreach (var parameter in parameters)
        {
            var schema = parameter.GetProperty("schema");
            var type = schema.GetProperty("type");
            Assert.True(type.ValueKind == JsonValueKind.String ? type.GetString() == "string" : type.EnumerateArray().Any(t => t.GetString() == "string"));
            Assert.Contains("without JSON quotes", schema.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.False(schema.TryGetProperty("pattern", out _));
        }
    }

    internal static JsonElement Resolve(JsonElement document, JsonElement schema)
    {
        if (schema.TryGetProperty("oneOf", out var branches))
        {
            var nonNull = branches.EnumerateArray().Where(item => !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "null").ToArray();
            if (nonNull.Length == 1)
            {
                return Resolve(document, nonNull[0]);
            }
        }
        while (schema.TryGetProperty("$ref", out var reference))
        {
            schema = document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        }
        return schema;
    }

    internal static void AssertOutput(JsonElement schema, bool nullable)
    {
        var type = schema.GetProperty("type");
        if (nullable)
        {
            Assert.Equal(new[] { "null", "string" }, type.EnumerateArray().Select(t => t.GetString()).Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Equal("string", type.GetString());
        }
        Assert.False(schema.TryGetProperty("oneOf", out _));
        Assert.Equal("^-?(0|[1-9][0-9]*)$", schema.GetProperty("pattern").GetString());
    }

    private static void AssertInput(JsonElement schema, bool nullable)
    {
        var branches = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(nullable ? 3 : 2, branches.Length);
        var text = Assert.Single(branches, item => item.GetProperty("type").GetString() == "string");
        Assert.Equal("^[+-]?[0-9]+$", text.GetProperty("pattern").GetString());
        var number = Assert.Single(branches, item => item.GetProperty("type").GetString() == "integer");
        Assert.Equal("int64", number.GetProperty("format").GetString());
        Assert.Equal(long.MinValue, number.GetProperty("minimum").GetInt64());
        Assert.Equal(long.MaxValue, number.GetProperty("maximum").GetInt64());
    }

    public sealed record SharedModel(long Id, long? Version, long[] Values, SharedModel? Child, Dictionary<string, long?> Totals, long?[] OptionalValues);
}
