using System.Net.Http.Json;
using System.Text.Json;
using NexusStackNext.Costing.Infrastructure;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.PricingHost;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class FactDeliveryRecoveryOpenApiTests(JourneyDatabaseTemplates databases)
{
    [Theory]
    [InlineData("platform")]
    [InlineData("identity")]
    [InlineData("scheduling")]
    public async Task PlatformHostMemory_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts(string source)
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await VerifyDocumentAsync(client, source);
    }

    [PostgresFact]
    public Task PlatformPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
        => VerifyPlatformPostgresAsync("platform");

    [PostgresFact]
    public Task IdentityPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
        => VerifyPlatformPostgresAsync("identity");

    [PostgresFact]
    public Task FilesPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
        => VerifyPlatformPostgresAsync("files");

    [PostgresFact]
    public Task SchedulingPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
        => VerifyPlatformPostgresAsync("scheduling");

    [PostgresFact]
    public async Task PricingPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
    {
        await using var database = await databases.CreateAsync("pricing");
        await using var host = await BusinessProcess.StartAsync(typeof(PricingHostMarker).Assembly.Location,
            "Pricing", database.ConnectionString);
        await VerifyDocumentAsync(host.Client, "pricing");
    }

    [Fact]
    public async Task FilesMemory_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        await VerifyDocumentAsync(client, "files");
    }

    [PostgresFact]
    public async Task CostingPostgres_DeclaresConditionalRecoveryInputsAndSafeExactResponseContracts()
    {
        await using var database = await databases.CreateAsync("costing");
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location,
            "Costing", database.ConnectionString);
        await VerifyDocumentAsync(host.Client, "costing");
    }

    private async Task VerifyPlatformPostgresAsync(string source)
    {
        await using var database = await databases.CreateAsync();
        await using var app = new PersistentIdentityApp(database.ConnectionString, schedulingWorkerEnabled: false);
        using var client = app.CreateClient();
        await VerifyDocumentAsync(client, source);
    }

    private static async Task VerifyDocumentAsync(HttpClient client, string source)
    {
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var paths = document.GetProperty("paths");
        var path = $"/api/{source}/audit-deliveries";
        var retry = paths.GetProperty(path + "/{messageId}/retry").GetProperty("post");
        AssertResponses(retry, "400", "401", "403", "409", "415", "503");
        var input = HttpInt64OpenApiTests.Resolve(document, retry.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema"));
        var inputProperties = input.GetProperty("properties");
        Assert.Equal(new[] { "expectedDeadLetteredAt", "expectedRetryRevision", "reason", "requestId" },
            inputProperties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Contains("expectedRetryRevision", input.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("uuid", inputProperties.GetProperty("requestId").GetProperty("format").GetString());
        Assert.Equal("date-time", inputProperties.GetProperty("expectedDeadLetteredAt").GetProperty("format").GetString());
        var branches = inputProperties.GetProperty("expectedRetryRevision").GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, branches.Length);
        var text = Assert.Single(branches, branch => branch.GetProperty("type").GetString() == "string");
        Assert.Equal("^[+-]?[0-9]+$", text.GetProperty("pattern").GetString());
        var integer = Assert.Single(branches, branch => branch.GetProperty("type").GetString() == "integer");
        Assert.Equal("int64", integer.GetProperty("format").GetString());
        Assert.Equal(long.MinValue, integer.GetProperty("minimum").GetInt64());
        Assert.Equal(long.MaxValue, integer.GetProperty("maximum").GetInt64());

        var receipt = DataSchema(document, retry).GetProperty("properties");
        Assert.Equal(new[] { "actorId", "execution", "expectedDeadLetteredAt", "expectedRetryRevision", "messageId",
            "reason", "recoveredAt", "requestId", "retainUntil", "retryRevision", "source" },
            receipt.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        HttpInt64OpenApiTests.AssertOutput(receipt.GetProperty("expectedRetryRevision"), nullable: false);
        HttpInt64OpenApiTests.AssertOutput(receipt.GetProperty("retryRevision"), nullable: false);
        foreach (var name in new[] { "expectedDeadLetteredAt", "recoveredAt", "retainUntil" })
        {
            Assert.Equal("date-time", receipt.GetProperty(name).GetProperty("format").GetString());
        }
        var readReceipt = paths.GetProperty(path + "/recoveries/{requestId}").GetProperty("get");
        AssertResponses(readReceipt, "401", "403", "404", "503");
        Assert.Equal(receipt.GetRawText(), DataSchema(document, readReceipt).GetProperty("properties").GetRawText());

        var single = paths.GetProperty(path + "/{messageId}").GetProperty("get");
        AssertResponses(single, "401", "403", "404", "503");
        var state = DataSchema(document, single).GetProperty("properties");
        Assert.Equal(new[] { "attempts", "deadLetteredAt", "messageId", "nextAttemptAt", "retryRevision", "state" },
            state.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        HttpInt64OpenApiTests.AssertOutput(state.GetProperty("retryRevision"), nullable: false);
        var list = paths.GetProperty(path).GetProperty("get");
        AssertResponses(list, "400", "401", "403");
        var listed = HttpInt64OpenApiTests.Resolve(document, DataSchema(document, list).GetProperty("items"));
        Assert.Equal(state.GetRawText(), listed.GetProperty("properties").GetRawText());

        var capacity = paths.GetProperty(path + "/recovery-capacity").GetProperty("get");
        AssertResponses(capacity, "401", "403", "503");
        var diagnostic = DataSchema(document, capacity).GetProperty("properties");
        Assert.Equal(new[] { "capacity", "isPersistent", "source" },
            diagnostic.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var limits = HttpInt64OpenApiTests.Resolve(document, diagnostic.GetProperty("capacity")).GetProperty("properties");
        foreach (var name in new[] { "maxRecords", "maxPayloadBytes", "retainedRecords", "retainedPayloadBytes" })
        {
            HttpInt64OpenApiTests.AssertOutput(limits.GetProperty(name), nullable: false);
        }
    }

    private static JsonElement DataSchema(JsonElement document, JsonElement operation)
    {
        var envelope = HttpInt64OpenApiTests.Resolve(document, operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        return HttpInt64OpenApiTests.Resolve(document, envelope.GetProperty("properties").GetProperty("data"));
    }

    private static void AssertResponses(JsonElement operation, params string[] codes)
    {
        foreach (var code in codes)
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(code, out _), $"Missing recovery response {code}.");
        }
    }
}
