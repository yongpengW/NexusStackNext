using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactCapacityPolicyOpenApiTests(JourneyDatabaseTemplates databases)
{
    [Fact]
    public Task PlatformMemory_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyMemoryAsync("platform");

    [Fact]
    public Task IdentityMemory_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyMemoryAsync("identity");

    [Fact]
    public Task FilesMemory_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyMemoryAsync("files");

    [Fact]
    public Task SchedulingMemory_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyMemoryAsync("scheduling");

    [PostgresFact]
    public Task PlatformPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public async Task CostingPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
    {
        await using var database = await databases.CreateAsync("costing");
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        await VerifyDocumentAsync(host.Client, "costing");
    }

    [PostgresFact]
    public async Task PricingPostgres_PublishesOnlyPolicyInputsAndExactResponseContracts()
    {
        await using var database = await databases.CreateAsync("pricing");
        await using var host = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location, "Pricing", database.ConnectionString);
        await VerifyDocumentAsync(host.Client, "pricing");
    }

    private static async Task VerifyMemoryAsync(string context)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await VerifyDocumentAsync(client, context);
    }

    private async Task VerifyPlatformPostgresAsync(string context)
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await VerifyDocumentAsync(client, context);
    }

    private static async Task VerifyDocumentAsync(HttpClient client, string context)
    {
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var path = $"/api/{context}/audit-capacity";
        FactCapacityAccessTests.AssertCapacitySchema(document, path);
        var operations = document.GetProperty("paths").GetProperty(path);
        var write = operations.GetProperty("put");
        foreach (var code in new[] { "400", "401", "403", "409", "415", "503" })
        {
            Assert.True(write.GetProperty("responses").TryGetProperty(code, out _), $"Missing documented policy result {code}.");
        }
        var input = HttpInt64OpenApiTests.Resolve(document, write.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema")).GetProperty("properties");
        Assert.Equal(new[] { "expectedPolicyRevision", "maxPayloadBytes", "maxRecordPayloadBytes", "maxRecords", "reason", "requestId" },
            input.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "expectedPolicyRevision", "maxPayloadBytes", "maxRecords" })
        {
            var branches = input.GetProperty(name).GetProperty("oneOf").EnumerateArray().ToArray();
            Assert.Equal(2, branches.Length);
            var text = Assert.Single(branches, item => item.GetProperty("type").GetString() == "string");
            Assert.Equal("^[+-]?[0-9]+$", text.GetProperty("pattern").GetString());
            var integer = Assert.Single(branches, item => item.GetProperty("type").GetString() == "integer");
            Assert.Equal("int64", integer.GetProperty("format").GetString());
            Assert.Equal(long.MinValue, integer.GetProperty("minimum").GetInt64());
            Assert.Equal(long.MaxValue, integer.GetProperty("maximum").GetInt64());
        }
        AssertInt32(input.GetProperty("maxRecordPayloadBytes"));
        Assert.Equal("uuid", input.GetProperty("requestId").GetProperty("format").GetString());
        var snapshot = DataSchema(document, operations.GetProperty("get"));
        HttpInt64OpenApiTests.AssertOutput(snapshot.GetProperty("policyRevision"), nullable: false);
        Assert.False(snapshot.TryGetProperty("business", out _));
        var control = HttpInt64OpenApiTests.Resolve(document, snapshot.GetProperty("controlCapacity")).GetProperty("properties");
        foreach (var name in new[] { "maxRecords", "maxPayloadBytes", "retainedRecords", "retainedPayloadBytes" })
        {
            HttpInt64OpenApiTests.AssertOutput(control.GetProperty(name), nullable: false);
        }
        AssertInt32(control.GetProperty("maxRecordPayloadBytes"));
        var receipt = DataSchema(document, write);
        Assert.Equal(new[] { "acceptedAt", "changed", "current", "eventId", "policyRevision", "previous", "requestId", "retainUntil" },
            receipt.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        HttpInt64OpenApiTests.AssertOutput(receipt.GetProperty("policyRevision"), nullable: false);
        Assert.Equal("boolean", receipt.GetProperty("changed").GetProperty("type").GetString());
        foreach (var side in new[] { "previous", "current" })
        {
            var limits = HttpInt64OpenApiTests.Resolve(document, receipt.GetProperty(side)).GetProperty("properties");
            HttpInt64OpenApiTests.AssertOutput(limits.GetProperty("maxRecords"), nullable: false);
            HttpInt64OpenApiTests.AssertOutput(limits.GetProperty("maxPayloadBytes"), nullable: false);
            AssertInt32(limits.GetProperty("maxRecordPayloadBytes"));
        }
        Assert.Equal("date-time", receipt.GetProperty("acceptedAt").GetProperty("format").GetString());
        Assert.Equal("date-time", receipt.GetProperty("retainUntil").GetProperty("format").GetString());
    }

    private static JsonElement DataSchema(JsonElement document, JsonElement operation)
    {
        var envelope = HttpInt64OpenApiTests.Resolve(document, operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        return HttpInt64OpenApiTests.Resolve(document, envelope.GetProperty("properties").GetProperty("data")).GetProperty("properties");
    }

    private static void AssertInt32(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()).ToArray() : [type.GetString()];
        Assert.Contains("integer", types);
        Assert.DoesNotContain("null", types);
    }
}
